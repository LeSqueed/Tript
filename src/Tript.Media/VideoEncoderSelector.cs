// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Media;

public sealed class VideoEncoderSelector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<VideoEncoder> _candidates;
    private readonly Func<VideoEncoder, FfmpegOutcome> _probe;
    private readonly Func<FfmpegOutcome>? _toneMapProbe;
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private VideoEncoder? _current;
    private bool? _gpuToneMapping;

    public VideoEncoderSelector(string ffmpegPath)
        : this(VideoEncoder.PlatformHardware(),
            candidate => FfmpegRunner.RunBounded(ffmpegPath, ProbeArgs(candidate), ProbeTimeout),
            () => FfmpegRunner.RunBounded(ffmpegPath, ToneMapProbeArgs(), ProbeTimeout))
    {
    }

    internal VideoEncoderSelector(IReadOnlyList<VideoEncoder> candidates, Func<VideoEncoder, FfmpegOutcome> probe,
        Func<FfmpegOutcome>? toneMapProbe = null)
    {
        _candidates = candidates;
        _probe = probe;
        _toneMapProbe = toneMapProbe;
    }

    public static VideoEncoderSelector SoftwareOnly() =>
        new([], _ => new FfmpegOutcome(true, 0, string.Empty));

    public VideoEncoder Current
    {
        get
        {
            lock (_gate)
                return _current ??= Select();
        }
    }

    public bool GpuToneMapping
    {
        get
        {
            lock (_gate)
                return _gpuToneMapping ??= ProbeToneMapping();
        }
    }

    public void DemoteToneMapping()
    {
        lock (_gate)
            _gpuToneMapping = false;
    }

    public void Demote(VideoEncoder failed)
    {
        if (!failed.IsHardware)
            return;

        lock (_gate)
        {
            _rejected.Add(failed.Name);
            if (ReferenceEquals(_current, failed))
                _current = null;
        }
    }

    private VideoEncoder Select()
    {
        var tried = new List<string>();
        foreach (var candidate in _candidates)
        {
            if (_rejected.Contains(candidate.Name))
                continue;

            var outcome = _probe(candidate);
            if (outcome.Succeeded)
            {
                Diagnostics.Report(DiagnosticLevel.Information,
                    $"Clips: encoding with {candidate.Name} (GPU){Tried(tried)}");
                return candidate;
            }

            _rejected.Add(candidate.Name);
            tried.Add($"{candidate.Name} ({FfmpegRunner.Tail(outcome.StandardError, 200).Trim()})");
        }

        if (_candidates.Count > 0)
            Diagnostics.Report(DiagnosticLevel.Warning,
                $"Clips: no GPU encoder worked, encoding on the CPU with libx264{Tried(tried)}");
        return VideoEncoder.Software;
    }

    private bool ProbeToneMapping()
    {
        if (_toneMapProbe is null)
            return false;

        var outcome = _toneMapProbe();
        if (outcome.Succeeded)
        {
            Diagnostics.Report(DiagnosticLevel.Information, "Clips: tone mapping HDR on the GPU (libplacebo on Vulkan)");
            return true;
        }

        Diagnostics.Report(DiagnosticLevel.Information,
            "Clips: tone mapping HDR on the CPU, Vulkan is unavailable: "
            + FfmpegRunner.Tail(outcome.StandardError, 200).Trim());
        return false;
    }

    private static string Tried(List<string> tried) =>
        tried.Count == 0 ? string.Empty : "; tried " + string.Join("; ", tried);

    internal static IReadOnlyList<string> ProbeArgs(VideoEncoder encoder)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        args.AddRange(encoder.GlobalArgs);
        args.AddRange(["-f", "lavfi", "-i", "color=black:s=320x240:r=30"]);
        if (encoder.FilterSuffix.Length > 0)
            args.AddRange(["-vf", encoder.FilterSuffix.TrimStart(',')]);
        args.AddRange(["-frames:v", "15", "-c:v", encoder.Name]);
        args.AddRange(encoder.OutputArgs);
        args.AddRange(["-f", "null", "-"]);
        return args;
    }

    internal static IReadOnlyList<string> ToneMapProbeArgs()
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        args.AddRange(ColorChain.GpuToneMapDevice);
        args.AddRange(
        [
            "-f", "lavfi",
            "-i", "color=black:s=320x240:r=30,format=yuv420p10le,"
                + "setparams=colorspace=bt2020nc:color_primaries=bt2020:color_trc=smpte2084",
            "-vf", ColorChain.GpuToneMapChain,
            "-frames:v", "5",
            "-f", "null", "-",
        ]);
        return args;
    }
}
