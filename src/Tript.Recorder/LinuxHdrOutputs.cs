// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace Tript.Recorder;

public sealed record DisplayOutput(string Name, int Width, int Height, bool Hdr);

public static class LinuxHdrOutputs
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<DisplayOutput> Query()
    {
        if (!OperatingSystem.IsLinux())
            return [];

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SWAYSOCK")))
            return Run("swaymsg", ["-t", "get_outputs", "-r"]) is { } sway ? ParseSway(sway) : [];

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
            return Run("kscreen-doctor", ["-j"]) is { } kscreen ? ParseKScreen(kscreen) : [];

        return [];
    }

    public static IReadOnlyList<DisplayOutput> ParseSway(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var outputs = new List<DisplayOutput>();
            foreach (var output in document.RootElement.EnumerateArray())
            {
                if (output.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
                    continue;
                if (!output.TryGetProperty("current_mode", out var mode)
                    || Integer(mode, "width") is not { } width || Integer(mode, "height") is not { } height)
                {
                    continue;
                }

                outputs.Add(new DisplayOutput(Text(output, "name") ?? string.Empty, width, height,
                    Flag(output, "hdr_enabled") ?? Flag(output, "hdr") ?? false));
            }

            return outputs;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<DisplayOutput> ParseKScreen(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("outputs", out var list) || list.ValueKind != JsonValueKind.Array)
                return [];

            var outputs = new List<DisplayOutput>();
            foreach (var output in list.EnumerateArray())
            {
                if (Flag(output, "enabled") == false || CurrentModeSize(output) is not { } size)
                    continue;

                outputs.Add(new DisplayOutput(Text(output, "name") ?? string.Empty, size.Width, size.Height,
                    Flag(output, "hdr") ?? Flag(output, "hdrEnabled") ?? false));
            }

            return outputs;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool? CaptureIsPq(IReadOnlyList<DisplayOutput> outputs, int captureWidth, int captureHeight)
    {
        ArgumentNullException.ThrowIfNull(outputs);

        if (outputs.Count == 0)
            return null;

        var matching = outputs.Where(output => output.Width == captureWidth && output.Height == captureHeight).ToList();
        var candidates = matching.Count > 0 ? matching : outputs.ToList();

        if (candidates.All(output => output.Hdr))
            return true;
        if (candidates.All(output => !output.Hdr))
            return false;
        return null;
    }

    private static (int Width, int Height)? CurrentModeSize(JsonElement output)
    {
        var modeId = Text(output, "currentModeId");
        if (modeId is null || !output.TryGetProperty("modes", out var modes) || modes.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var mode in modes.EnumerateArray())
        {
            if (Text(mode, "id") != modeId || !mode.TryGetProperty("size", out var size))
                continue;
            if (Integer(size, "width") is { } width && Integer(size, "height") is { } height)
                return (width, height);
        }

        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static int? Integer(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

    private static string? Run(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(QueryTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                                              or InvalidOperationException or IOException)
        {
            Log.Debug(exception, "Recorder: {Program} could not report the display outputs", program);
            return null;
        }
    }
}
