// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;

namespace Tript.Shell;

internal static class StartupFailureDialog
{
    internal sealed record Command(string FileName, IReadOnlyList<string> Arguments);

    internal static Command? Choose(string text, Func<string, bool> isInstalled)
    {
        if (isInstalled("zenity"))
            return new Command("zenity", ["--error", "--title=Tript", "--no-markup", $"--text={text}"]);
        if (isInstalled("kdialog"))
            return new Command("kdialog", ["--title", "Tript", "--error", text]);
        if (isInstalled("notify-send"))
            return new Command("notify-send", ["--app-name=Tript", "--urgency=critical", "Tript", text]);
        return null;
    }

    internal static void Show(string text)
    {
        Console.Error.WriteLine(text);

        if (Choose(text, IsOnPath) is not { } command)
            return;

        try
        {
            var start = new ProcessStartInfo(command.FileName) { UseShellExecute = false };
            foreach (var argument in command.Arguments)
                start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            process?.WaitForExit(TimeSpan.FromMinutes(10));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static bool IsOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, tool)));
}
