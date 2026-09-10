// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public sealed record ObsGameCaptureTarget(string? WindowTitle, string? WindowClass, string? ExecutablePath)
{
    public bool IsEmpty => WindowTitle is null && WindowClass is null && ExecutablePath is null;
}

internal enum WindowPriority
{
    Title = 0,
    Class = 1,
    Exe = 2
}

public static class ObsCaptureSource
{
    private const string WindowKey = "window";
    private const string PriorityKey = "priority";

    public static IReadOnlyList<ObsSourceProperty>? GetGameCaptureProperties()
    {
        var properties = DiscoverGameCaptureProperties();
        return properties.Count == 0 ? null : properties;
    }

    public static ObsSettings? BuildGameCaptureSettings(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var properties = DiscoverGameCaptureProperties();
        if (properties.Count == 0)
            return null;

        var settings = new ObsSettings();
        ApplyTarget(settings, properties, target);
        return settings;
    }

    public static bool Retarget(ObsSource source, ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var properties = DiscoverGameCaptureProperties();
        if (properties.Count == 0)
            return false;

        using var settings = new ObsSettings();
        if (!ApplyTarget(settings, properties, target))
            return false;

        source.Update(settings);
        return true;
    }

    private const string WindowMatchWildcard = "*";

    public static string BuildWindowMatchString(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return string.Join(
            ':',
            EncodeWindowPart(target.WindowTitle) ?? WindowMatchWildcard,
            EncodeWindowPart(target.WindowClass) ?? WindowMatchWildcard,
            EncodeWindowPart(target.ExecutablePath) ?? WindowMatchWildcard);
    }

    internal static WindowPriority ResolveWindowPriority(ObsGameCaptureTarget target) =>
        target.ExecutablePath is not null ? WindowPriority.Exe
        : target.WindowTitle is not null ? WindowPriority.Title
        : WindowPriority.Class;

    private static string? EncodeWindowPart(string? part) =>
        string.IsNullOrEmpty(part) ? null : part.Replace("#", "#22").Replace(":", "#3A");

    public static IReadOnlyList<string> DisplayCaptureIdPreference(bool isWindows, bool preferPortal)
    {
        if (isWindows)
            return ["monitor_capture", "display_capture"];

        return preferPortal
            ? ["pipewire-desktop-capture-source", "xshm_input"]
            : ["xshm_input", "pipewire-desktop-capture-source"];
    }

    public static string? SelectDisplayCaptureId(IReadOnlyList<string> registeredTypeIds, IReadOnlyList<string> preference)
    {
        ArgumentNullException.ThrowIfNull(registeredTypeIds);
        ArgumentNullException.ThrowIfNull(preference);

        foreach (var candidate in preference)
        {
            foreach (var registered in registeredTypeIds)
            {
                if (string.Equals(registered, candidate, StringComparison.Ordinal))
                    return candidate;
            }
        }

        return null;
    }

    public static string? FindDisplayCaptureId() =>
        SelectDisplayCaptureId(
            ObsSourceProperties.EnumerateTypeIds(),
            DisplayCaptureIdPreference(
                OperatingSystem.IsWindows(),
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))));

    public static ObsSettings BuildDisplayCaptureSettings(ObsSource source, int displayIndex)
    {
        ArgumentNullException.ThrowIfNull(source);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, source.EnumerateProperties(), displayIndex);
        return settings;
    }

    public static ObsSettings BuildDisplayCaptureSettings(string typeId, int displayIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, ObsSourceProperties.EnumerateTypeProperties(typeId), displayIndex);
        return settings;
    }

    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        return FindDisplaySelection(ObsSourceProperties.EnumerateTypeProperties(typeId))?.Items ?? [];
    }

    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return FindDisplaySelection(source.EnumerateProperties())?.Items ?? [];
    }

    public static IReadOnlyList<ObsDisplay> EnumerateDisplays(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DescribeDisplays(source.EnumerateProperties());
    }

    public static IReadOnlyList<ObsDisplay> EnumerateDisplays(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        return DescribeDisplays(ObsSourceProperties.EnumerateTypeProperties(typeId));
    }

    public static IReadOnlyList<ObsDisplay> DescribeDisplays(IReadOnlyList<ObsSourceProperty> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        if (FindDisplaySelection(properties) is not { } property)
            return [];

        var displays = new List<ObsDisplay>(property.Items.Count);
        var origin = -1;

        for (var index = 0; index < property.Items.Count; index++)
        {
            var item = property.Items[index];

            var id = item.Format == ObsComboFormat.String
                ? item.Value as string
                : Convert.ToInt64(item.Value ?? 0L).ToString();

            if (string.IsNullOrEmpty(id))
                continue;

            var (name, width, height, atOrigin) = ObsDisplayLabel.Parse(item.Name, index);
            if (atOrigin && origin < 0)
                origin = displays.Count;

            displays.Add(new ObsDisplay(id, name, index, width, height, Primary: false));
        }

        if (displays.Count == 0)
            return displays;

        var primary = origin < 0 ? 0 : origin;
        displays[primary] = displays[primary] with { Primary = true };
        return displays;
    }

    public static ObsDisplayResolution ResolveDisplay(IReadOnlyList<ObsDisplay> displays, string? preferredId)
    {
        ArgumentNullException.ThrowIfNull(displays);

        var fallback = displays.FirstOrDefault(display => display.Primary) ?? displays.FirstOrDefault();
        if (string.IsNullOrEmpty(preferredId))
            return new ObsDisplayResolution(fallback, RequestedMissing: false);

        var match = displays.FirstOrDefault(display => string.Equals(display.Id, preferredId, StringComparison.Ordinal));
        return match is not null
            ? new ObsDisplayResolution(match, RequestedMissing: false)
            : new ObsDisplayResolution(fallback, RequestedMissing: true);
    }

    public static ObsSettings BuildDisplayCaptureSettings(ObsSource source, ObsDisplay display)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(display);

        var settings = new ObsSettings();
        ApplyDisplay(settings, source.EnumerateProperties(), display);
        return settings;
    }

    private static IReadOnlyList<ObsSourceProperty> DiscoverGameCaptureProperties() =>
        ObsSourceProperties.EnumerateTypeProperties("game_capture");

    private static bool ApplyTarget(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, ObsGameCaptureTarget target)
    {
        if (target.IsEmpty)
            return false;

        var wrote = false;

        if (FindKey(properties, WindowKey) is { } windowKey)
        {
            settings.SetString(windowKey, BuildWindowMatchString(target));
            wrote = true;
        }

        if (FindKey(properties, PriorityKey) is { } priorityKey)
        {
            settings.SetInt(priorityKey, (int)ResolveWindowPriority(target));
            wrote = true;
        }

        return wrote;
    }

    private static readonly string[] DisplaySelectionKeys = ["screen", "monitor_id", "display", "monitor"];

    private static ObsSourceProperty? FindDisplaySelection(IReadOnlyList<ObsSourceProperty> properties)
    {
        foreach (var key in DisplaySelectionKeys)
        {
            foreach (var property in properties)
            {
                if (property.Type == ObsPropertyType.List &&
                    string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property;
                }
            }
        }

        foreach (var property in properties)
        {
            if (property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static void ApplyDisplayIndex(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, int displayIndex)
    {
        if (FindDisplaySelection(properties) is not { } property)
            return;

        if (property.Items.Count > 0 &&
            property.Items[0].Format == ObsComboFormat.String)
        {
            var index = Math.Clamp(displayIndex, 0, property.Items.Count - 1);
            if (property.Items[index].Value is string deviceId && deviceId.Length > 0)
                settings.SetString(property.Name, deviceId);

            return;
        }

        settings.SetInt(property.Name, displayIndex);
    }

    private static void ApplyDisplay(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, ObsDisplay display)
    {
        if (FindDisplaySelection(properties) is not { } property)
            return;

        if (property.Items.Count > 0 && property.Items[0].Format == ObsComboFormat.String)
        {
            settings.SetString(property.Name, display.Id);
            return;
        }

        settings.SetInt(property.Name, int.TryParse(display.Id, out var screen) ? screen : display.Index);
    }

    private static string? FindKey(IReadOnlyList<ObsSourceProperty> properties, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var property in properties)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Name;
            }
        }

        return null;
    }
}
