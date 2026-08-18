// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

// The file side of settings persistence. The store works in text: read the file (or the default
// empty object if it is absent or blank), write the serialized model back.
namespace Tript.Settings;

public sealed class SettingsFileProvider
{
    public string FilePath { get; }

    public SettingsFileProvider(string filePath)
    {
        FilePath = filePath;
    }

    // The file's text, or null when the file does not exist or is blank — the caller (the store)
    // decides what "no file" means, which is defaults, not an error.
    public string? ReadJson()
    {
        if (!File.Exists(FilePath))
            return null;

        var text = File.ReadAllText(FilePath);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public void WriteRaw(string json)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(FilePath, json);
    }
}
