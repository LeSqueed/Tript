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

        // Open with ReadWrite|Delete sharing, the same way RecordingMetadataStore reads: a plain
        // ReadAllText (FileShare.Read) cannot be open while AtomicFile's replace-rename needs the
        // target's delete access, and on Windows the writer then fails with an access denial.
        // Linux has no sharing model, so this changes nothing there.
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

        // Atomic: the settings file holds the recording directory, the game list and the audio
        // routing, and a torn write leaves a blank file that loads as defaults and is then persisted
        // over the wreckage.
        AtomicFile.WriteAllText(FilePath, json);
    }
}
