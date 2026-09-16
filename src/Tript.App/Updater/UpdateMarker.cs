// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Updater;

// Read by launcher.c, which has no JSON parser. Line 1: format version, line 2: target
// version, line 3: staged folder name under <installRoot>\.tript-update\.
internal sealed record UpdateMarker(int FormatVersion, string Version, string StagedFolderName)
{
    internal const int CurrentFormatVersion = 1;

    internal static UpdateMarker? TryRead(string path)
    {
        string content;
        try
        {
            if (!File.Exists(path))
                return null;
            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 3 || !int.TryParse(lines[0], out var formatVersion)
            || formatVersion != CurrentFormatVersion
            || string.IsNullOrWhiteSpace(lines[1]) || string.IsNullOrWhiteSpace(lines[2])
            || lines[2].Contains("..", StringComparison.Ordinal)
            || lines[2].Contains('/') || lines[2].Contains('\\'))
        {
            return null;
        }

        return new UpdateMarker(formatVersion, lines[1].Trim(), lines[2].Trim());
    }

    internal static void WriteAtomic(string path, UpdateMarker marker)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var content = $"{marker.FormatVersion}\n{marker.Version}\n{marker.StagedFolderName}\n";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
