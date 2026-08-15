// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Text;

namespace Tript.Media;

// Runs an ffmpeg process with a prepared argument list and reports its stderr as progress. The
// argument list is passed via ProcessStartInfo.ArgumentList, which handles per-argument quoting for
// the platform — a single Arguments string would be split by the shell or, with UseShellExecute
// false, passed to execve with literal quote characters (measured: ffmpeg received the quotes and
// failed with exit code 234).
public static class FfmpegRunner
{
    public static void Run(string ffmpegPath, IReadOnlyList<string> args, ClipRequest request, string stage)
    {
        request.Progress?.Invoke(ClipProgress.At(stage, string.Join(' ', args)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            if (!process.Start())
                throw new ClipSourceException($"Failed to start ffmpeg: {ffmpegPath}");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ClipSourceException($"Failed to start ffmpeg at '{ffmpegPath}': {ex.Message}", ex);
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr) stderr.AppendLine(e.Data);
            request.Progress?.Invoke(ClipProgress.At(stage, e.Data));
        };

        process.BeginErrorReadLine();

        // Drain stdout (unused for clips) so the child cannot block on a full pipe.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        stdoutTask.Wait();

        if (process.ExitCode != 0)
        {
            string detail;
            lock (stderr) detail = stderr.ToString();
            throw new ClipEncodeException(
                $"ffmpeg failed during {stage} with exit code {process.ExitCode}."
                + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail.Trim()),
                process.ExitCode);
        }
    }
}
