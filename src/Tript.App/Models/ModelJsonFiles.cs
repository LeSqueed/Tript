// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Serilog;

namespace Tript.App.Models;

internal static class ModelJsonFiles
{
    internal static readonly JsonSerializerOptions Options = new(Wire.Options)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal static T? Read<T>(string? path) where T : class
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException exception)
        {
            Log.Warning(exception, "GameModelManager: ignoring invalid JSON at {Path}", path);
            return null;
        }
    }

    internal static void WriteAtomic<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
