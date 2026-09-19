// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using Serilog;
using Tript.Settings;

namespace Tript.Shell;

internal sealed class WindowPlacementStore
{
    private readonly SettingsFileProvider _provider;

    internal WindowPlacementStore()
        : this(new SettingsFileProvider(SettingsFilePaths.WindowStatePath))
    {
    }

    internal WindowPlacementStore(SettingsFileProvider provider)
    {
        _provider = provider;
    }

    internal WindowPlacement? Load()
    {
        try
        {
            var json = _provider.ReadJson();
            return json is null
                ? null
                : JsonSerializer.Deserialize<WindowPlacement>(json, SettingsSerialization.Options);
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Debug(exception, "Tript.Shell: could not read the saved window placement");
            return null;
        }
    }

    internal void Save(WindowPlacement placement)
    {
        try
        {
            _provider.WriteRaw(JsonSerializer.Serialize(placement, SettingsSerialization.Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Debug(exception, "Tript.Shell: could not save the window placement");
        }
    }
}
