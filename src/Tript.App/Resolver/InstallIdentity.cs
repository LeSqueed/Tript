// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Tript.Settings;

namespace Tript.App.Resolver;

internal static class InstallIdentity
{
    private const string FileName = "install-id.json";

    private static readonly Lazy<string> _value = new(LoadOrCreate);

    internal static string Value => _value.Value;

    private static string LoadOrCreate()
    {
        var path = Path.Combine(SettingsFilePaths.EnsureConfigDirectory(), FileName);
        try
        {
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<InstallIdFile>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(existing?.InstallId))
                    return existing.InstallId;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        var id = Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new InstallIdFile { InstallId = id }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return id;
    }

    private sealed class InstallIdFile
    {
        public string InstallId { get; set; } = string.Empty;
    }
}
