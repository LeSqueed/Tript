// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Nodes;
using Tript.App.Ipc;
using Tript.Obs;
using Tript.Settings;
using SettingsModel = Tript.Settings.Settings;

namespace Tript.App;

internal static class SettingsMessage
{
    internal static JsonElement Build(SettingsModel settings, IReadOnlyList<AudioDeviceSetting> audioDevices,
        IReadOnlyList<ObsDisplay>? displays, IReadOnlyList<string>? availableEncoders,
        DisplaySize? primaryDisplay, string? appVersion)
    {
        var settingsNode = JsonSerializer.SerializeToNode(settings, SettingsSerialization.Options);

        if (settingsNode?["audio"] is JsonObject audioNode)
            audioNode["devices"] = JsonSerializer.SerializeToNode(audioDevices, SettingsSerialization.Options);
        var settingsElement = JsonSerializer.Deserialize<JsonElement>(
            settingsNode?.ToJsonString() ?? "{}", SettingsSerialization.Options);
        return JsonSerializer.SerializeToElement(new
        {
            settings = settingsElement,

            availableEncoders,

            displayResolution = primaryDisplay is { IsUsable: true } display
                ? (object?)new { width = display.Width, height = display.Height }
                : null,

            availableDisplays = displays?.Select(monitor => new
            {
                id = monitor.Id,
                name = monitor.Name,
                width = monitor.Width,
                height = monitor.Height,
                primary = monitor.Primary,
            }).ToList(),
            displayFallbackWarning = DisplayFallbackWarning(settings.Capture, displays),

            appVersion,
        }, Wire.Options);
    }

    internal static object? DisplayFallbackWarning(CaptureSettings capture, IReadOnlyList<ObsDisplay>? displays)
    {
        if (displays is null || string.IsNullOrEmpty(capture.Display))
            return null;

        var resolution = ObsCaptureSource.ResolveDisplay(displays, capture.Display);
        if (!resolution.RequestedMissing)
            return null;

        return new
        {
            requestedId = capture.Display,
            requestedLabel = capture.DisplayLabel,
            usingId = resolution.Selected?.Id,
            usingLabel = resolution.Selected?.Name,
        };
    }
}
