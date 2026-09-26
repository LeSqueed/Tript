// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;

namespace Tript.Recorder;

internal static class CaptureSourceFactory
{
    private const string GameCaptureId = ObsCaptureSource.WinGameCaptureId;
    private const string VkCaptureWindowKey = "window";
    private const string VkCaptureAnyClient = "";
    private const string CaptureModeKey = "capture_mode";
    private const string WindowCaptureModeValue = "window";
    private const string HookRateKey = "hook_rate";
    private const long FastestHookRate = 3;

    internal static ObsSource? CreateDisplay(string? preferredDisplayId, string? portalRestoreToken,
        out ObsDisplay? selected)
    {
        selected = null;

        if (ObsCaptureSource.FindDisplayCaptureId() is not { } displayId)
        {
            Log.Warning("ObsRecorderSession: no display-capture source type is registered; " +
                        "an unhooked game capture will record the background only.");
            return null;
        }

        var source = CreateDisplaySource(displayId, portalRestoreToken);
        try
        {
            var displays = ObsCaptureSource.EnumerateDisplays(source);
            var resolution = ObsCaptureSource.ResolveDisplay(displays, preferredDisplayId);
            selected = resolution.Selected;

            if (resolution.RequestedMissing)
            {
                Log.Warning("ObsRecorderSession: the selected monitor {RequestedId} is not attached; " +
                            "capturing {UsingName} ({UsingId}) for this session. The preference is kept.",
                    preferredDisplayId, selected?.Name, selected?.Id);
            }

            if (selected is null && ObsCaptureSource.IsPortalCapture(displayId))
            {
                Log.Information("ObsRecorderSession: {DisplayId} lets the desktop choose the screen", displayId);
            }
            else if (selected is null)
            {
                Log.Warning("ObsRecorderSession: {DisplayId} enumerated no monitors; " +
                            "the display layer keeps the plugin's default.", displayId);
            }
            else
            {
                using var settings = ObsCaptureSource.BuildDisplayCaptureSettings(source, selected);
                source.Update(settings);
                Log.Information("ObsRecorderSession: display layer is {DisplayId} on {Name} ({Id}) {Width}x{Height}",
                    displayId, selected.Name, selected.Id, selected.Width, selected.Height);
            }
        }
        catch
        {
            source.Dispose();
            throw;
        }

        return source;
    }

    private static ObsSource CreateDisplaySource(string displayId, string? portalRestoreToken)
    {
        if (!ObsCaptureSource.IsPortalCapture(displayId))
            return ObsSource.CreatePrivate(displayId, "app display");

        using var settings = new ObsSettings();
        if (!string.IsNullOrEmpty(portalRestoreToken))
            settings.SetString(ObsCaptureSource.PortalRestoreTokenKey, portalRestoreToken);

        Log.Information("ObsRecorderSession: capturing the screen through the desktop portal ({Consent})",
            string.IsNullOrEmpty(portalRestoreToken) ? "asking for consent" : "reusing the remembered consent");
        return ObsSource.CreatePrivate(displayId, "app display", settings);
    }

    internal static ObsSource? CreateGame(ObsGameCaptureTarget? target) =>
        ObsCaptureSource.FindGameCaptureId() switch
        {
            ObsCaptureSource.WinGameCaptureId => CreateWinGameCapture(target),
            ObsCaptureSource.VkCaptureId => CreateVkCapture(),
            _ => null
        };

    internal static void RetargetVkCapture(ObsSource source, string? clientName)
    {
        using var settings = new ObsSettings();
        settings.SetString(VkCaptureWindowKey, clientName ?? VkCaptureAnyClient);
        source.Update(settings);
    }

    private static ObsSource CreateVkCapture()
    {
        using var settings = new ObsSettings();
        settings.SetString(VkCaptureWindowKey, VkCaptureAnyClient);

        Log.Information("ObsRecorderSession: games are captured through obs-vkcapture (the most recent client)");
        return ObsSource.CreatePrivate(ObsCaptureSource.VkCaptureId, "app capture", settings);
    }

    private static ObsSource? CreateWinGameCapture(ObsGameCaptureTarget? target)
    {
        var properties = ObsSourceProperties.EnumerateTypeProperties(GameCaptureId);
        if (properties.Count == 0)
            return null;

        using var settings = target is not null
            ? ObsCaptureSource.BuildGameCaptureSettings(target) ?? new ObsSettings()
            : new ObsSettings();

        if (ResolveWindowCaptureMode(properties) is { } mode)
        {
            settings.SetString(CaptureModeKey, mode);
        }
        else
        {
            Log.Warning("ObsRecorderSession: game capture declares no '{Key}' property; " +
                        "writing '{Value}' anyway rather than leaving it on any_fullscreen.",
                CaptureModeKey, WindowCaptureModeValue);
            settings.SetString(CaptureModeKey, WindowCaptureModeValue);
        }

        settings.SetInt(HookRateKey, FastestHookRate);

        return ObsSource.CreatePrivate(GameCaptureId, "app capture", settings);
    }

    internal static string? ResolveWindowCaptureMode(IReadOnlyList<ObsSourceProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        foreach (var property in properties)
        {
            if (property.Type != ObsPropertyType.List ||
                !string.Equals(property.Name, CaptureModeKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var windowMode = property.Items.FirstOrDefault(item =>
                item.Format == ObsComboFormat.String &&
                item.Value is string itemValue &&
                string.Equals(itemValue, WindowCaptureModeValue, StringComparison.Ordinal));

            return windowMode.Value as string ?? WindowCaptureModeValue;
        }

        return null;
    }
}
