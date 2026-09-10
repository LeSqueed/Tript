// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

public sealed class SettingsFileProvider
{
    public string FilePath { get; }

    public SettingsFileProvider(string filePath)
    {
        FilePath = filePath;
    }

    public string? ReadJson()
    {
        if (!File.Exists(FilePath))
            return null;

        string text;
        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void WriteRaw(string json)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        AtomicFile.WriteAllText(FilePath, json);
    }
}
