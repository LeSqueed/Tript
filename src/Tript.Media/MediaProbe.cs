// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tript.Media;

public sealed class MediaProbe
{
    private readonly string _ffprobePath;

    private readonly Dictionary<string, MediaInfo> _cache = new(StringComparer.Ordinal);

    public MediaProbe(string ffprobePath)
    {
        _ffprobePath = ffprobePath;
    }

    public MediaInfo Probe(string path)
    {
        var absolute = Path.GetFullPath(path);
        lock (_cache)
        {
            if (_cache.TryGetValue(absolute, out var cached))
                return cached;
        }

        var info = ProbeUncached(absolute);
        lock (_cache)
        {
            _cache[absolute] = info;
        }

        return info;
    }

    private MediaInfo ProbeUncached(string path)
    {
        if (!File.Exists(path))
            throw new ClipSourceException($"Source file does not exist: {path}");

        var arguments = new List<string>
        {
            "-v", "error",
            "-show_entries",
            "stream=codec_type,codec_name,profile,width,height,r_frame_rate,avg_frame_rate,"
                + "pix_fmt,color_space,color_transfer,color_primaries,color_range,index",
            "-show_entries",
            "format=duration",
            "-of", "json",
            path,
        };

        var (stdout, stderr, exitCode) = Run(_ffprobePath, arguments);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new ClipSourceException($"ffprobe could not read '{path}'. {detail.Trim()}");
        }

        try
        {
            return Parse(stdout, path);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
        {
            throw new ClipSourceException($"ffprobe output for '{path}' was not understood: {ex.Message}");
        }
    }

    internal static MediaInfo Parse(string ffprobeJson, string path)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var root = doc.RootElement;

        var stream = FirstStream(root, "streams");
        var format = FirstObject(root, "format");

        var transfer = GetString(stream, "color_transfer") ?? "unspecified";
        var primaries = GetString(stream, "color_primaries") ?? "unspecified";
        var space = GetString(stream, "color_space") ?? "unspecified";
        var range = GetString(stream, "color_range") ?? "unspecified";

        var frameRate = ParseFrameRate(stream);
        var duration = double.TryParse(GetString(format, "duration"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

        var audioCount = 0;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var codecType = GetString(s, "codec_type");
                if (codecType == "audio") audioCount++;
            }
        }

        return new MediaInfo
        {
            CodecName = GetString(stream, "codec_name") ?? throw new KeyNotFoundException("codec_name"),
            Profile = GetString(stream, "profile"),
            Width = GetInt(stream, "width"),
            Height = GetInt(stream, "height"),
            FrameRate = frameRate,
            PixelFormat = GetString(stream, "pix_fmt") ?? "unspecified",
            ColorSpace = space,
            ColorTransfer = transfer,
            ColorPrimaries = primaries,
            ColorRange = range,
            DurationSeconds = duration,
            AudioStreamCount = audioCount,
        };
    }

    private static Fraction ParseFrameRate(JsonElement stream)
    {
        var raw = GetString(stream, "avg_frame_rate") ?? GetString(stream, "r_frame_rate") ?? "0/1";
        var parts = raw.Split('/');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            return new Fraction(num, den);
        }

        throw new FormatException($"Unparseable frame rate '{raw}'");
    }

    private static JsonElement FirstStream(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
            throw new KeyNotFoundException(property);
        foreach (var item in element.EnumerateArray())
        {
            if (GetString(item, "codec_type") == "video")
                return item;
        }

        throw new KeyNotFoundException($"{property} has no video stream");
    }

    private static JsonElement FirstObject(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
            throw new KeyNotFoundException(property);
        return element;
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new FormatException($"Expected integer '{name}', got nothing");

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var asInt))
            return asInt;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asStr))
        {
            return asStr;
        }

        throw new FormatException($"Expected integer '{name}', got '{value}'");
    }

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    internal static (string Stdout, string Stderr, int ExitCode) Run(string fileName,
        IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in arguments)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start {fileName}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ClipSourceException($"Failed to start {fileName}: {ex.Message}", ex);
        }

        ProcessPipes.LowerPriority(process);
        var stdout = ProcessPipes.BeginRead(process.StandardOutput);
        var stderr = ProcessPipes.BeginRead(process.StandardError);

        var limit = timeout ?? ProbeTimeout;
        if (!process.WaitForExit((int)limit.TotalMilliseconds))
        {
            ProcessPipes.KillQuietly(process);
            ProcessPipes.Settle(stdout, stderr);
            return (string.Empty,
                $"{Path.GetFileName(fileName)} did not exit within {limit.TotalSeconds:0.#}s and was killed.",
                -1);
        }

        ProcessPipes.Settle(stdout, stderr);
        return (ProcessPipes.TextOf(stdout), ProcessPipes.TextOf(stderr), process.ExitCode);
    }

    internal static string Quote(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
}
