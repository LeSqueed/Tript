// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;

namespace Tript.Media;

// Turns marked regions of a recorded session into clip files. Everything downstream of a finished
// recording: probe the source, decide how colour is handled, cut each region at its exact time
// (never keyframe-aligned) and re-encode so no broken first frame survives the cut, then write the
// file(s) with ffmpeg.
//
// The exact-time rule overrides stream-copy. The spec's stream-copy-when-frame-rate-matches is a
// separate optimisation for preserving source quality when no re-encode is needed; a clip cut at an
// exact marked time is re-encoded by definition, because stream-copying a non-keyframe boundary
// leaves a frame that depends on a keyframe the clip does not contain.
public sealed class ClipEngine : IClipEngine
{
    private readonly string _ffmpegPath;
    private readonly MediaProbe _probe;

    public ClipEngine(string ffmpegPath, MediaProbe probe)
    {
        _ffmpegPath = ffmpegPath;
        _probe = probe;
    }

    public IReadOnlyList<string> CreateClips(ClipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceInfo = Validate(request);

        return request.Mode == ClipMode.Combine
            ? CreateCombined(request, sourceInfo)
            : CreateSeparate(request, sourceInfo);
    }

    // ---- Validation ----

    private MediaInfo Validate(ClipRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            throw new ClipSourceException("A source file path is required.");
        if (request.Regions is null || request.Regions.Count == 0)
            throw new ClipSourceException("At least one region is required to create a clip.");
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new ClipSourceException("An output path is required.");

        var sourceInfo = _probe.Probe(request.SourcePath);

        foreach (var region in request.Regions)
        {
            if (region.Start < TimeSpan.Zero)
                throw new ClipSourceException($"Region starts before the recording begins: {region.Start}.");
            if (region.End <= region.Start)
                throw new ClipSourceException($"Region end ({region.End}) is not after its start ({region.Start}).");
            if (region.End > TimeSpan.FromSeconds(sourceInfo.DurationSeconds))
                throw new ClipSourceException(
                    $"Region end {FormatSeconds(region.End.TotalSeconds)} is beyond the recording's duration "
                    + $"({FormatSeconds(sourceInfo.DurationSeconds)}s).");
        }

        return sourceInfo;
    }

    // ---- The clip extraction decision ----

    // Three inputs decide: is the source HDR, can the target codec carry 10-bit, and do all
    // segments share one transfer. The transfer rule is checked before extraction begins, across
    // all segments — a mix of HDR and SDR source segments has no single colour volume to preserve,
    // so preservation is disabled for the whole clip.
    private static ColorPlan ResolveColorPlan(MediaInfo sourceInfo, IReadOnlyList<ClipRegion> regions,
        string encoderFamily)
    {
        var allShareTransfer = regions
            .Select(_ => sourceInfo.ColorTransfer)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;

        var canPreserve = sourceInfo.IsHdr && allShareTransfer && CanCarry10Bit(encoderFamily);

        if (canPreserve)
        {
            return new ColorPlan
            {
                Decision = ColorDecision.Preserve,
                PreservingHdr = true,
                TransferToCarry = sourceInfo.ColorTransfer,
                ToneMapping = false,
                ForcesPixelFormat = true,
                EncoderFamily = encoderFamily,
            };
        }

        var toneMapping = sourceInfo.IsHdr;
        var mixedSdr = !sourceInfo.IsHdr && IsMixedTransfer(sourceInfo, regions);

        return new ColorPlan
        {
            Decision = toneMapping ? ColorDecision.ToneMap : ColorDecision.Preserve,
            PreservingHdr = false,
            TransferToCarry = null,
            ToneMapping = toneMapping,
            // SDR with mixed segments still forces an explicit yuv420p. For a single source file
            // the transfer is uniform, so this only fires on the tone-map path and the mixed-SDR
            // path.
            ForcesPixelFormat = toneMapping || mixedSdr,
            EncoderFamily = encoderFamily,
        };
    }

    // "Segments with differing transfers cannot be preserved, only tone-mapped." For a single
    // source file every region shares the file's transfer by construction, but the rule is
    // evaluated over the region list so a future multi-input engine cannot skip it.
    private static bool IsMixedTransfer(MediaInfo sourceInfo, IReadOnlyList<ClipRegion> regions) =>
        regions.Select(_ => sourceInfo.ColorTransfer).Distinct(StringComparer.Ordinal).Count() > 1;

    private static bool CanCarry10Bit(string encoderFamily) => encoderFamily switch
    {
        "libx265" => true,
        "nvenc" or "amf" or "qsv" => true,
        _ => false,
    };

    // ---- Combine: several regions, one output file ----

    private IReadOnlyList<string> CreateCombined(ClipRequest request, MediaInfo sourceInfo)
    {
        var outputPath = Path.GetFullPath(request.OutputPath);
        EnsureOutputDirectory(outputPath);

        var colorPlan = ResolveColorPlan(sourceInfo, request.Regions, request.EncoderFamily);
        var audioTrackCount = sourceInfo.AudioStreamCount;
        var regionCount = request.Regions.Count;

        var args = new List<string>();

        // One input per region, each seeking exactly to its start and taking exactly its duration.
        // Inputs are opened independently from the same file, so each seek is exact.
        for (var i = 0; i < regionCount; i++)
        {
            args.Add("-ss");
            args.Add(FormatSeconds(request.Regions[i].Start.TotalSeconds));
            args.Add("-t");
            args.Add(FormatSeconds(request.Regions[i].Duration.TotalSeconds));
            args.Add("-i");
            args.Add(request.SourcePath);
        }

        // The whole filtergraph lives in -filter_complex: per-region audio adjustments, concat,
        // then the colour chain applied to the joined video stream.
        var filter = BuildCombineFilter(request, regionCount, audioTrackCount, colorPlan);
        args.Add("-filter_complex");
        args.Add(filter);

        args.Add("-map");
        args.Add("[vout]");
        for (var t = 0; t < audioTrackCount; t++)
        {
            args.Add("-map");
            args.Add($"[aout{t}]");
        }

        AppendVideoEncodeArgs(args, colorPlan);
        AppendAudioEncodeArgs(args);

        args.Add(outputPath);

        FfmpegRunner.Run(_ffmpegPath, args, request, "combine");
        return [outputPath];
    }

    // Builds the -filter_complex graph for Combine.
    //
    // Per region r and track t:
    //   [r:a:t]volume=...  or  [r:a:t]anull   -> [ar{r}t{t}]
    // Video inputs flow through ([0:v], [1:v], ...).
    // Then concat: [0:v][1:v]...[audio in region/track order]concat=n=R:v=1:a=R*T[vcat][c0..c{T-1}]
    // Then the colour chain on [vcat] -> [vout].
    private static string BuildCombineFilter(ClipRequest request, int regionCount,
        int audioTrackCount, ColorPlan colorPlan)
    {
        var parts = new List<string>();
        var concatInputs = new List<string>();

        for (var r = 0; r < regionCount; r++)
        {
            concatInputs.Add($"[{r}:v]");

            for (var t = 0; t < audioTrackCount; t++)
            {
                var input = $"[{r}:a:{t}]";
                var adj = request.AudioTrackAdjustments.FirstOrDefault(a => a.SourceTrackIndex == t);
                if (adj.Muted)
                {
                    parts.Add($"{input}volume=0[ar{r}t{t}]");
                    concatInputs.Add($"[ar{r}t{t}]");
                }
                else if (adj.Volume != 1.0)
                {
                    var vol = adj.Volume.ToString("0.####", CultureInfo.InvariantCulture);
                    parts.Add($"{input}volume={vol}[ar{r}t{t}]");
                    concatInputs.Add($"[ar{r}t{t}]");
                }
                else
                {
                    parts.Add($"{input}anull[ar{r}t{t}]");
                    concatInputs.Add($"[ar{r}t{t}]");
                }
            }
        }

        // concat filter: all video inputs first, then all audio inputs in (region, track) order.
        // The concat filter's a= is the number of audio inputs PER segment, not the total.
        var concatArgs = $"concat=n={regionCount}:v=1:a={audioTrackCount}";
        var inputs = string.Concat(concatInputs);
        var concatOutputs = "[vcat]";
        for (var t = 0; t < audioTrackCount; t++)
            concatOutputs += $"[c{t}]";
        parts.Add($"{inputs}{concatArgs}{concatOutputs}");

        // The colour chain on the joined video.
        parts.Add($"[vcat]{BuildVideoChain(colorPlan)}[vout]");

        // Pass the concat audio outputs through labelled as aout{t}.
        for (var t = 0; t < audioTrackCount; t++)
            parts.Add($"[c{t}]anull[aout{t}]");

        return string.Join(",", parts);
    }

    // ---- Separate: each region its own file ----

    private IReadOnlyList<string> CreateSeparate(ClipRequest request, MediaInfo sourceInfo)
    {
        var outputDirectory = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(outputDirectory);

        var colorPlan = ResolveColorPlan(sourceInfo, request.Regions, request.EncoderFamily);
        var audioTrackCount = sourceInfo.AudioStreamCount;

        var results = new List<string>();
        for (var i = 0; i < request.Regions.Count; i++)
        {
            var region = request.Regions[i];
            var outputPath = Path.Combine(outputDirectory, BuildFileName(request.SourcePath, region, i));

            var args = new List<string>
            {
                "-ss",
                FormatSeconds(region.Start.TotalSeconds),
                "-t",
                FormatSeconds(region.Duration.TotalSeconds),
                "-i",
                request.SourcePath,
            };

            // The colour chain and the audio adjustments share one filtergraph.
            var filter = BuildSeparateFilter(request, audioTrackCount, colorPlan);
            args.Add("-filter_complex");
            args.Add(filter);

            args.Add("-map");
            args.Add("[vout]");
            for (var t = 0; t < audioTrackCount; t++)
            {
                args.Add("-map");
                args.Add($"[aout{t}]");
            }

            AppendVideoEncodeArgs(args, colorPlan);
            AppendAudioEncodeArgs(args);

            args.Add(outputPath);

            FfmpegRunner.Run(_ffmpegPath, args, request, $"separate region {i + 1}/{request.Regions.Count}");
            results.Add(outputPath);
        }

        return results;
    }

    // [0:v] -> colour chain -> [vout]; [0:a:t] -> volume/anull -> [aout{t}].
    private static string BuildSeparateFilter(ClipRequest request, int audioTrackCount, ColorPlan colorPlan)
    {
        var parts = new List<string>();

        parts.Add($"[0:v]{BuildVideoChain(colorPlan)}[vout]");

        for (var t = 0; t < audioTrackCount; t++)
        {
            var input = $"[0:a:{t}]";
            var adj = request.AudioTrackAdjustments.FirstOrDefault(a => a.SourceTrackIndex == t);
            if (adj.Muted)
                parts.Add($"{input}volume=0[aout{t}]");
            else if (adj.Volume != 1.0)
            {
                var vol = adj.Volume.ToString("0.####", CultureInfo.InvariantCulture);
                parts.Add($"{input}volume={vol}[aout{t}]");
            }
            else
                parts.Add($"{input}anull[aout{t}]");
        }

        return string.Join(",", parts);
    }

    // The video filter chain, chosen by the colour plan and always ending in the correct colour
    // tags. Top-level -color_trc/-color_primaries flags are unreliable (libx264 dropped them),
    // so the tagging is part of the graph via setparams.
    private static string BuildVideoChain(ColorPlan colorPlan)
    {
        if (colorPlan.ToneMapping)
            return ColorChain.ToneMapChain;

        if (colorPlan.PreservingHdr)
        {
            // Preserve: stream passes through untouched (encoder args carry 10-bit + main10), and
            // the colour tags are setparams: bt2020nc / bt2020 / the source's own transfer.
            return $"null,{ColorChain.TagPreserved(colorPlan.TransferToCarry!)}";
        }

        if (colorPlan.ForcesPixelFormat)
            return $"{ColorChain.ForceYuv420p},{ColorChain.TagBt709}";

        return $"null,{ColorChain.TagBt709}";
    }

    // ---- Encoder + colour tag argument builders ----

    private static void AppendVideoEncodeArgs(List<string> args, ColorPlan colorPlan)
    {
        if (colorPlan.PreservingHdr)
        {
            // Preserve: 10-bit libx265 with main10. The bt2020nc/bt2020/transfer tags are applied
            // in the filter graph (setparams), not here — top-level colour flags are unreliable.
            args.Add("-c:v");
            args.Add("libx265");
            args.Add("-pix_fmt");
            args.Add("yuv420p10le");
            args.Add("-profile:v");
            args.Add("main10");
            return;
        }

        // Tone-map or SDR: H.264 for broad compatibility. The BT.709 tags are applied in the
        // filter graph.
        args.Add("-c:v");
        args.Add("libx264");
    }

    private static void AppendAudioEncodeArgs(List<string> args)
    {
        args.Add("-c:a");
        args.Add("aac");
    }

    // ---- helpers ----

    private static string BuildFileName(string sourcePath, ClipRegion region, int index)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var start = region.Start.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
        var end = region.End.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
        return $"{baseName}-clip-{index + 1}-{start}s-{end}s.mp4";
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.#######", CultureInfo.InvariantCulture);

    private static void EnsureOutputDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
