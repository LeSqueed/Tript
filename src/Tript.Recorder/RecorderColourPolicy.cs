// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

internal static class RecorderColourPolicy
{
    private const string ForceSdrKey = "force_sdr";

    internal static HdrPlan Resolve(ObsRuntime runtime, ResolvedRecorderSettings settings)
    {
        var candidates = ObsEncoderPolicy.EnumerateVideoEncoderCandidates();
        if (candidates.Count == 0)
            throw new ObsException("No loaded module registers a usable video encoder.");

        var configured = ObsEncoderPolicy.IsUsableId(settings.Encoder) ? settings.Encoder : null;

        var plan = HdrPlanner.Decide(
            CaptureColourSpace(runtime),
            HdrDisplayProbe.AnyDisplayIsHdr(),
            settings.EnableHdr,
            candidates,
            configured);

        Log.Information("ObsRecorderSession: recording in {Colour} with '{Encoder}': {Reason}.",
            plan.UseHdr ? "HDR (Rec.2100 PQ, 10-bit P010)" : "SDR (Rec.709)", plan.EncoderId, plan.Reason);

        return plan;
    }

    internal static HdrPlan ApplyToCanvas(ObsRuntime runtime, HdrPlan plan, Action onCanvasReset,
        Action<HdrPlan> applyCaptureColour)
    {
        if (!runtime.TryGetVideoInfo(out var current) || current is null)
            return plan;

        if (current.OutputFormat == plan.OutputFormat && current.ColorSpace == plan.ColorSpace)
            return plan;

        var result = runtime.ResetVideo(current with
        {
            OutputFormat = plan.OutputFormat,
            ColorSpace = plan.ColorSpace
        });

        if (result == ObsVideoResetResult.Success)
        {
            onCanvasReset();
            return plan;
        }

        if (!plan.UseHdr)
        {
            Log.Warning("ObsRecorderSession: obs_reset_video refused the SDR canvas ({Result}); " +
                        "the mix keeps its current colour space.", result);
            return plan;
        }

        Log.Warning("ObsRecorderSession: obs_reset_video refused the HDR canvas ({Result}); " +
                    "recording SDR instead.", result);

        var downgraded = HdrPlanner.Decide(
            capturedColorSpace: ObsSourceColorSpace.Srgb,
            displayIsHdr: false,
            hdrEnabledInSettings: false,
            ObsEncoderPolicy.EnumerateVideoEncoderCandidates(),
            configuredEncoderId: null);

        applyCaptureColour(downgraded);
        return downgraded;
    }

    internal static void ApplyForceSdr(ObsSource? source, bool forceSdr)
    {
        if (source is null)
            return;

        try
        {
            using var settings = new ObsSettings();
            settings.SetBool(ForceSdrKey, forceSdr);
            source.Update(settings);
        }
        catch (ObsException exception)
        {
            Log.Debug(exception, "ObsRecorderSession: could not set '{Key}' on a capture source.", ForceSdrKey);
        }
    }

    private static ObsSourceColorSpace? CaptureColourSpace(ObsRuntime runtime)
    {
        var probes = runtime.ProbeDisplays();
        if (probes.Count == 0)
            return null;

        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            Log.Debug("ObsRecorderSession: display probe: {Probes}", string.Join("; ",
                probes.Select(p => $"[{p.MonitorIndex}] {p.ColorSpace} @ {p.SdrWhiteLevelNits} nits")));

        var choice = DisplayColourResolver.Choose(probes);

        if (choice.SdrWhiteLevelNits > 0f)
            runtime.SetVideoLevels(choice.SdrWhiteLevelNits, ObsRuntime.DefaultHdrNominalPeakLevelNits);

        return choice.ColourSpace;
    }
}
