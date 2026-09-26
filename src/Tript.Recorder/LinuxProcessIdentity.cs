// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.Recorder;

internal sealed record ProcessIdentity(string Executable, string? ExecutablePath);

internal interface IProcessFiles
{
    string? ReadCommandLine(int processId);

    string? ReadExecutableLink(int processId);

    string? ReadEnvironmentVariable(int processId, string name);

    string? ReadCommandName(int processId);
}

internal sealed class ProcProcessFiles : IProcessFiles
{
    public string? ReadCommandLine(int processId) => ReadText(processId, "cmdline");

    public string? ReadCommandName(int processId) => ReadText(processId, "comm")?.TrimEnd('\n');

    public string? ReadExecutableLink(int processId)
    {
        try
        {
            return new FileInfo(ProcPath(processId, "exe")).LinkTarget;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadEnvironmentVariable(int processId, string name)
    {
        var prefix = name + "=";
        return ReadText(processId, "environ")?
            .Split('\0')
            .FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    private static string? ReadText(int processId, string file)
    {
        try
        {
            return File.ReadAllText(ProcPath(processId, file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ProcPath(int processId, string file) => $"/proc/{processId}/{file}";
}

internal static class LinuxProcessIdentity
{
    private const string WindowsExtension = ".exe";
    private const string DeletedSuffix = " (deleted)";
    private const string WinePrefixVariable = "WINEPREFIX";

    private static readonly HashSet<string> WineLoaders = new(StringComparer.Ordinal)
    {
        "wine", "wine64", "wine-preloader", "wine64-preloader", "wineloader",
    };

    internal static ProcessIdentity? Read(IProcessFiles files, int processId)
    {
        var arguments = SplitCommandLine(files.ReadCommandLine(processId));
        if (TryFindWindowsExecutable(arguments, out var windowsPath))
        {
            var winePrefix = NeedsWinePrefix(windowsPath)
                ? files.ReadEnvironmentVariable(processId, WinePrefixVariable)
                : null;
            return new ProcessIdentity(FilePaths.FileName(windowsPath), ToLinuxPath(windowsPath, winePrefix));
        }

        var executable = files.ReadExecutableLink(processId);
        if (string.IsNullOrEmpty(executable))
            return null;

        if (executable.EndsWith(DeletedSuffix, StringComparison.Ordinal))
            executable = executable[..^DeletedSuffix.Length];

        return IsWineLoader(executable) ? null : new ProcessIdentity(FilePaths.FileName(executable), executable);
    }

    internal static IReadOnlyList<string> SplitCommandLine(string? commandLine)
        => string.IsNullOrEmpty(commandLine)
            ? []
            : commandLine.TrimEnd('\0').Split('\0');

    internal static bool TryFindWindowsExecutable(IReadOnlyList<string> arguments, out string windowsPath)
    {
        windowsPath = string.Empty;
        var program = Unquoted(arguments.SkipWhile(IsWineLoader).FirstOrDefault()?.Trim());
        if (string.IsNullOrEmpty(program))
            return false;

        var end = program.EndsWith(WindowsExtension, StringComparison.OrdinalIgnoreCase)
            ? program.Length
            : EndOfExecutableFollowedByArguments(program);
        if (end < 0)
            return false;

        windowsPath = program[..end];
        return FilePaths.FileName(windowsPath).Length > WindowsExtension.Length;
    }

    internal static string? ToLinuxPath(string windowsPath, string? winePrefix)
    {
        if (windowsPath.StartsWith('/'))
            return windowsPath;

        if (!HasDriveLetter(windowsPath))
            return null;

        var rest = windowsPath[2..].Replace('\\', '/').TrimStart('/');
        var drive = char.ToLowerInvariant(windowsPath[0]);
        if (drive == 'z')
            return "/" + rest;

        return string.IsNullOrWhiteSpace(winePrefix) || !winePrefix.StartsWith('/')
            ? null
            : $"{winePrefix.TrimEnd('/')}/dosdevices/{drive}:/{rest}";
    }

    private static bool NeedsWinePrefix(string windowsPath)
        => HasDriveLetter(windowsPath) && char.ToLowerInvariant(windowsPath[0]) != 'z';

    private static bool HasDriveLetter(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    private static string? Unquoted(string? argument)
    {
        if (argument is null || !argument.StartsWith('"'))
            return argument;

        var closing = argument.IndexOf('"', 1);
        return closing < 0 ? argument[1..] : argument[1..closing];
    }

    private static int EndOfExecutableFollowedByArguments(string program)
    {
        var index = program.IndexOf(WindowsExtension + " ", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? -1 : index + WindowsExtension.Length;
    }

    private static bool IsWineLoader(string argument)
        => WineLoaders.Contains(FilePaths.FileName(argument));
}
