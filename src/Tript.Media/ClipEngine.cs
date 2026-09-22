// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Globalization;
using Tript.Core;

namespace Tript.Media;

public sealed class ClipEngine : IClipEngine
{
    private readonly string _ffmpegPath;
    private readonly MediaProbe _probe;
    private readonly VideoEncoderSelector _encoders;

    public ClipEngine(string ffmpegPath, MediaProbe probe, VideoEncoderSelector? encoders = null)
    {
        _ffmpegPath = ffmpegPath;
        _probe = probe;
        _encoders = encoders ?? VideoEncoderSelector.SoftwareOnly();
    }

    public IReadOnlyList<string> CreateClips(ClipRequest request) =>
        CreateClipOutputs(request).Select(output => output.Path).ToList();

    public IReadOnlyList<ClipOutput> CreateClipOutputs(ClipRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (sourceInfo, regions) = Validate(request);

        if (request.PreferStreamCopy && !request.ForceSdr
            && request.Mode == ClipMode.Combine && regions.Count == 1)
            return SharingRegions(CreateStreamCopy(request, regions[0]), [regions[0]]);

        if (request.Mode == ClipMode.Combine)
            return SharingRegions(CreateCombined(request, sourceInfo, regions), regions);

        return CreateSeparate(request, sourceInfo, regions)
            .Select((path, index) => new ClipOutput(path, [regions[index]]))
            .ToList();
    }

    private static IReadOnlyList<ClipOutput> SharingRegions(
        IReadOnlyList<string> paths, IReadOnlyList<ClipRegion> regions) =>
        paths.Select(path => new ClipOutput(path, regions)).ToList();

    private (MediaInfo SourceInfo, IReadOnlyList<ClipRegion> Regions) Validate(ClipRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
            throw new ClipSourceException("A source file path is required.");
        if (request.Regions is null || request.Regions.Count == 0)
            throw new ClipSourceException("At least one region is required to create a clip.");
        if (string.IsNullOrWhiteSpace(request.OutputPath))
            throw new ClipSourceException("An output path is required.");

        if (string.Equals(Path.GetFullPath(request.SourcePath), Path.GetFullPath(request.OutputPath), FilePaths.Comparison))
            throw new ClipSourceException("The clip output must not overwrite its source file.");

        var sourceInfo = _probe.Probe(request.SourcePath);

        var regions = ClipRegionBounds.ClampAll(request.Regions, sourceInfo.DurationSeconds);
        if (regions.Count == 0)
        {
            var length = double.IsFinite(sourceInfo.DurationSeconds) && sourceInfo.DurationSeconds > 0
                ? $" The recording is {FormatSeconds(sourceInfo.DurationSeconds)}s long."
                : string.Empty;
            throw new ClipSourceException(
                "None of the marked regions falls inside the recording, so there was nothing to clip."
                + length);
        }

        return (sourceInfo, regions);
    }

    private static ColorPlan ResolveColorPlan(MediaInfo sourceInfo, IReadOnlyList<ClipRegion> regions,
        string encoderFamily, bool forceSdr)
    {
        var allShareTransfer = regions
            .Select(_ => sourceInfo.ColorTransfer)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1;

        var canPreserve = sourceInfo.IsHdr && !forceSdr && allShareTransfer && CanCarry10Bit(encoderFamily);

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

        var toneMapping = sourceInfo.IsHdr && (forceSdr || !CanCarry10Bit(encoderFamily));
        var mixedSdr = !sourceInfo.IsHdr && IsMixedTransfer(sourceInfo, regions);

        return new ColorPlan
        {
            Decision = toneMapping ? ColorDecision.ToneMap : ColorDecision.Preserve,
            PreservingHdr = false,
            TransferToCarry = null,
            ToneMapping = toneMapping,

            ForcesPixelFormat = toneMapping || mixedSdr,
            EncoderFamily = encoderFamily,
        };
    }

    private static bool IsMixedTransfer(MediaInfo sourceInfo, IReadOnlyList<ClipRegion> regions) =>
        regions.Select(_ => sourceInfo.ColorTransfer).Distinct(StringComparer.Ordinal).Count() > 1;

    private static bool CanCarry10Bit(string encoderFamily) => encoderFamily switch
    {
        "libx265" => true,
        "nvenc" or "amf" or "qsv" => true,
        _ => false,
    };

    private IReadOnlyList<string> CreateStreamCopy(ClipRequest request, ClipRegion region)
    {
        var outputPath = Path.GetFullPath(request.OutputPath);
        EnsureOutputDirectory(outputPath);

        var args = new List<string>
        {
            "-ss", FormatSeconds(region.Start.TotalSeconds),
            "-i", request.SourcePath,
            "-t", FormatSeconds(region.Duration.TotalSeconds),
            "-map", "0:v:0?",
            "-map", "0:a?",
            "-c", "copy",
            "-avoid_negative_ts", "make_zero",
            "-movflags", "+faststart",
            outputPath,
        };

        FfmpegRunner.Run(_ffmpegPath, args, request, "stream-copy");
        EnsureOutputWritten(outputPath);
        return [outputPath];
    }

    private IReadOnlyList<string> CreateCombined(ClipRequest request, MediaInfo sourceInfo,
        IReadOnlyList<ClipRegion> regions)
    {
        var outputPath = Path.GetFullPath(request.OutputPath);
        EnsureOutputDirectory(outputPath);

        var colorPlan = ResolveColorPlan(sourceInfo, regions, request.EncoderFamily, request.ForceSdr);
        var audioTrackCount = sourceInfo.AudioStreamCount;

        Encode(request, outputPath, "combine", colorPlan, plan =>
            BuildCombinedArgs(request, regions, audioTrackCount, colorPlan, plan, outputPath));
        return [outputPath];
    }

    private static List<string> BuildCombinedArgs(ClipRequest request, IReadOnlyList<ClipRegion> regions,
        int audioTrackCount, ColorPlan colorPlan, EncodePlan plan, string outputPath)
    {
        var encoder = plan.Encoder;
        var regionCount = regions.Count;
        var args = plan.GlobalArgs();

        for (var i = 0; i < regionCount; i++)
        {
            args.AddRange(encoder.InputArgs);
            args.Add("-ss");
            args.Add(FormatSeconds(regions[i].Start.TotalSeconds));
            args.Add("-t");
            args.Add(FormatSeconds(regions[i].Duration.TotalSeconds));
            args.Add("-i");
            args.Add(request.SourcePath);
        }

        var filter = BuildCombineFilter(request, regionCount, audioTrackCount, colorPlan, plan);
        args.Add("-filter_complex");
        args.Add(filter);

        args.Add("-map");
        args.Add("[vout]");
        for (var t = 0; t < audioTrackCount; t++)
        {
            args.Add("-map");
            args.Add($"[aout{t}]");
        }

        AppendVideoEncodeArgs(args, colorPlan, encoder);
        AppendAudioEncodeArgs(args);

        args.Add(outputPath);
        return args;
    }

    private void Encode(ClipRequest request, string outputPath, string stage, ColorPlan colorPlan,
        Func<EncodePlan, List<string>> buildArgs)
    {
        var plan = colorPlan.PreservingHdr
            ? EncodePlan.Cpu
            : new EncodePlan(_encoders.Current, colorPlan.ToneMapping && _encoders.GpuToneMapping);
        try
        {
            FfmpegRunner.Run(_ffmpegPath, buildArgs(plan), request, stage);
            EnsureOutputWritten(outputPath);
            return;
        }
        catch (ClipEncodeException exception) when (plan.UsesGpu)
        {
            Diagnostics.Report(DiagnosticLevel.Warning,
                $"Clips: {plan.Describe()} failed during {stage}, retrying on the CPU: "
                + FfmpegRunner.Tail(exception.Message));
            TryDelete(outputPath);
        }

        FfmpegRunner.Run(_ffmpegPath, buildArgs(EncodePlan.Cpu), request, $"{stage} (CPU retry)");
        EnsureOutputWritten(outputPath);
        _encoders.Demote(plan.Encoder);
        if (plan.GpuToneMap)
            _encoders.DemoteToneMapping();
    }

    private readonly record struct EncodePlan(VideoEncoder Encoder, bool GpuToneMap)
    {
        internal static EncodePlan Cpu => new(VideoEncoder.Software, false);

        internal bool UsesGpu => Encoder.IsHardware || GpuToneMap;

        internal List<string> GlobalArgs()
        {
            var args = new List<string>(Encoder.GlobalArgs);
            if (GpuToneMap)
                args.AddRange(ColorChain.GpuToneMapDevice);
            return args;
        }

        internal string Describe() => GpuToneMap ? $"{Encoder.Name} with GPU tone mapping" : Encoder.Name;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static AudioTrackAdjustment AdjustmentFor(ClipRequest request, int sourceTrackIndex) =>
        request.AudioTrackAdjustments.FirstOrDefault(
            adjustment => adjustment.SourceTrackIndex == sourceTrackIndex,
            new AudioTrackAdjustment(sourceTrackIndex));

    private static string BuildCombineFilter(ClipRequest request, int regionCount,
        int audioTrackCount, ColorPlan colorPlan, EncodePlan plan)
    {
        var parts = new List<string>();
        var concatInputs = new List<string>();

        for (var r = 0; r < regionCount; r++)
        {
            concatInputs.Add($"[{r}:v]");

            for (var t = 0; t < audioTrackCount; t++)
            {
                var input = $"[{r}:a:{t}]";
                var adj = AdjustmentFor(request, t);
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

        var concatArgs = $"concat=n={regionCount}:v=1:a={audioTrackCount}";
        var inputs = string.Concat(concatInputs);
        var concatOutputs = "[vcat]";
        for (var t = 0; t < audioTrackCount; t++)
            concatOutputs += $"[c{t}]";
        parts.Add($"{inputs}{concatArgs}{concatOutputs}");

        parts.Add($"[vcat]{BuildVideoChain(colorPlan, plan.GpuToneMap)}{plan.Encoder.FilterSuffix}[vout]");

        for (var t = 0; t < audioTrackCount; t++)
            parts.Add($"[c{t}]anull[aout{t}]");

        return string.Join(",", parts);
    }

    private IReadOnlyList<string> CreateSeparate(ClipRequest request, MediaInfo sourceInfo,
        IReadOnlyList<ClipRegion> regions)
    {
        var outputDirectory = Path.GetFullPath(request.OutputPath);
        Directory.CreateDirectory(outputDirectory);

        var colorPlan = ResolveColorPlan(sourceInfo, regions, request.EncoderFamily, request.ForceSdr);
        var audioTrackCount = sourceInfo.AudioStreamCount;

        var results = new List<string>();
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var outputPath = Path.Combine(outputDirectory, BuildFileName(request.SourcePath, region, i));

            Encode(request, outputPath, $"separate region {i + 1}/{regions.Count}", colorPlan, plan =>
                BuildSeparateArgs(request, region, audioTrackCount, colorPlan, plan, outputPath));
            results.Add(outputPath);
        }

        return results;
    }

    private static List<string> BuildSeparateArgs(ClipRequest request, ClipRegion region,
        int audioTrackCount, ColorPlan colorPlan, EncodePlan plan, string outputPath)
    {
        var encoder = plan.Encoder;
        var args = plan.GlobalArgs();
        args.AddRange(encoder.InputArgs);
        args.AddRange(
        [
            "-ss",
            FormatSeconds(region.Start.TotalSeconds),
            "-t",
            FormatSeconds(region.Duration.TotalSeconds),
            "-i",
            request.SourcePath,
        ]);

        var filter = BuildSeparateFilter(request, audioTrackCount, colorPlan, plan);
        args.Add("-filter_complex");
        args.Add(filter);

        args.Add("-map");
        args.Add("[vout]");
        for (var t = 0; t < audioTrackCount; t++)
        {
            args.Add("-map");
            args.Add($"[aout{t}]");
        }

        AppendVideoEncodeArgs(args, colorPlan, encoder);
        AppendAudioEncodeArgs(args);

        args.Add(outputPath);
        return args;
    }

    private static string BuildSeparateFilter(ClipRequest request, int audioTrackCount, ColorPlan colorPlan,
        EncodePlan plan)
    {
        var parts = new List<string>();

        parts.Add($"[0:v]{BuildVideoChain(colorPlan, plan.GpuToneMap)}{plan.Encoder.FilterSuffix}[vout]");

        for (var t = 0; t < audioTrackCount; t++)
        {
            var input = $"[0:a:{t}]";
            var adj = AdjustmentFor(request, t);
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

    private static string BuildVideoChain(ColorPlan colorPlan, bool gpuToneMap)
    {
        if (colorPlan.ToneMapping)
            return gpuToneMap ? ColorChain.GpuToneMapChain : ColorChain.ToneMapChain;

        if (colorPlan.PreservingHdr)
        {
            return $"null,{ColorChain.TagPreserved(colorPlan.TransferToCarry!)}";
        }

        if (colorPlan.ForcesPixelFormat)
            return $"{ColorChain.ForceYuv420p},{ColorChain.TagBt709}";

        return $"null,{ColorChain.TagBt709}";
    }

    private static void AppendVideoEncodeArgs(List<string> args, ColorPlan colorPlan, VideoEncoder encoder)
    {
        if (colorPlan.PreservingHdr)
        {
            args.Add("-c:v");
            args.Add("libx265");
            args.Add("-pix_fmt");
            args.Add("yuv420p10le");
            args.Add("-profile:v");
            args.Add("main10");
            return;
        }

        args.Add("-c:v");
        args.Add(encoder.Name);
        args.AddRange(encoder.OutputArgs);
    }

    private static void AppendAudioEncodeArgs(List<string> args)
    {
        args.Add("-c:a");
        args.Add("aac");
    }

    private static string BuildFileName(string sourcePath, ClipRegion region, int index)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        var start = region.Start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');
        var end = region.End.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', '_');
        return $"{baseName}-clip-{index + 1}-{start}s-{end}s.mp4";
    }

    private static void EnsureOutputWritten(string outputPath)
    {
        var file = new FileInfo(outputPath);
        if (!file.Exists)
            throw new ClipEncodeException($"ffmpeg reported success but wrote no file at '{outputPath}'.", 0);

        if (file.Length == 0)
            throw new ClipEncodeException($"ffmpeg reported success but wrote an empty file at '{outputPath}'.", 0);
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
