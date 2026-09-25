// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;

namespace Tript.Recorder;

public sealed class PortalRestoreTokenStore(string path)
{
    public const string FileName = "portal-restore-token";

    public string Path { get; } = path;

    public string? Load()
    {
        try
        {
            var token = File.ReadAllText(Path).Trim();
            return token.Length == 0 ? null : token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Forget()
    {
        try
        {
            if (!File.Exists(Path))
                return false;

            File.Delete(Path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Recorder: the remembered screen-share consent at {Path} could not be removed", Path);
            return false;
        }
    }

    public bool Save(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Trim() == Load())
            return false;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, token.Trim());
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Recorder: the screen-share consent could not be remembered at {Path}", Path);
            return false;
        }
    }
}
