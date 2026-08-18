// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// A target for game capture, carried as the capture source's own vocabulary so the caller never
// touches a plugin key. What a process identity must contain is exactly what the discovered
// properties accept: on Windows the game-capture plugin matches on a window by title or class and
// on an executable by name.
public sealed record ObsGameCaptureTarget(string? WindowTitle, string? WindowClass, string? ExecutablePath)
{
    public bool IsEmpty => WindowTitle is null && WindowClass is null && ExecutablePath is null;
}

// The field the game-capture plugin matches a window on when it searches for the target, mirroring
// win-capture's window_priority (WINDOW_PRIORITY_TITLE/CLASS/EXE = 0/1/2, the same values its
// "priority" property's list items carry). An exe-only target can only be matched by EXE.
internal enum WindowPriority
{
    Title = 0,
    Class = 1,
    Exe = 2
}

// The capture-source surface: creating the platform's game or display capture source, attaching it
// to a detected game, and re-targeting an existing source without restarting it. Deliberately not a
// factory with a mode→id table: which capture source to use is a per-platform and per-configuration
// decision that the recorder's orchestration makes.
public static class ObsCaptureSource
{
    // The three keys win-capture's game_capture actually reads for targeting. Named here because
    // the property enumeration is matched against them exactly; see FindKey.
    private const string WindowKey = "window";
    private const string PriorityKey = "priority";

    // ---- game-capture attachment ----

    // The settings keys game capture reads to attach to a window. Discovered from the source's own
    // properties rather than hardcoded: the plugin-side keys are not in the libobs headers, and
    // obs_get_source_properties is the discovery route.
    public static IReadOnlyList<ObsSourceProperty>? GetGameCaptureProperties()
    {
        var properties = DiscoverGameCaptureProperties();
        return properties.Count == 0 ? null : properties;
    }

    // The settings that attach a game-capture source to the given target, as a fresh object to pass
    // to CreatePrivate or Update. The keys come from the discovered properties: only a property
    // game capture declares is written.
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

    // Applies the target to an existing game-capture source without restarting it — the re-
    // targeting seam. The recorder calls this when the detected game re-creates its window mid-
    // session.
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

    // ---- the window match string ----

    // The part win-capture writes for a field it is not matching on. It has to be *something*:
    // ms_build_window_strings decodes an empty part to NULL, and ms_find_window returns NULL
    // outright when the class is NULL, so a "::game.exe" string never matches any window however
    // right the executable is. That single line is why an exe-only target must carry placeholders.
    private const string WindowMatchWildcard = "*";

    // The one string win-capture parses a target out of: "title:class:exe", split by
    // ms_build_window_strings (game-capture.c get_config) with ':' and '#' escaped as "#3A" and
    // "#22". Under WINDOW_PRIORITY_EXE only the executable has to match; the title then merely
    // ranks the candidates and the class is only tested for being present.
    public static string BuildWindowMatchString(ObsGameCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return string.Join(
            ':',
            EncodeWindowPart(target.WindowTitle) ?? WindowMatchWildcard,
            EncodeWindowPart(target.WindowClass) ?? WindowMatchWildcard,
            EncodeWindowPart(target.ExecutablePath) ?? WindowMatchWildcard);
    }

    // The field win-capture should match the target on. An executable name is the identity the game
    // detector actually has, and it is the only part that survives a game re-creating its window,
    // so it wins whenever it is known — which is also the plugin's own default.
    internal static WindowPriority ResolveWindowPriority(ObsGameCaptureTarget target) =>
        target.ExecutablePath is not null ? WindowPriority.Exe
        : target.WindowTitle is not null ? WindowPriority.Title
        : WindowPriority.Class;

    // Mirrors win-capture's encode_dstr, whose decode replaces "#3A" before "#22" — so '#' has to be
    // escaped first here or a literal "#3A" in a title would decode back to a colon.
    private static string? EncodeWindowPart(string? part) =>
        string.IsNullOrEmpty(part) ? null : part.Replace("#", "#22").Replace(":", "#3A");

    // ---- display capture ----

    // The display-capture source ids the platform capture modules register, and there is no shared
    // one: win-capture registers monitor_capture, linux-capture xshm_input on X11, and the
    // desktop-portal source is the Wayland route. Ordered by preference, so a runtime that
    // registers several gets the one that works on the session it is running in.
    public static IReadOnlyList<string> DisplayCaptureIdPreference(bool isWindows, bool preferPortal)
    {
        if (isWindows)
            return ["monitor_capture", "display_capture"];

        return preferPortal
            ? ["pipewire-desktop-capture-source", "xshm_input"]
            : ["xshm_input", "pipewire-desktop-capture-source"];
    }

    // The first preferred id this runtime actually registered, or null when none is — which is a
    // documented state, not an error: a recorder without a display layer keeps its background.
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

    // As SelectDisplayCaptureId, against the running libobs and this platform.
    public static string? FindDisplayCaptureId() =>
        SelectDisplayCaptureId(
            ObsSourceProperties.EnumerateTypeIds(),
            DisplayCaptureIdPreference(
                OperatingSystem.IsWindows(),
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))));

    // The settings that capture the given display, as a fresh object to pass to CreatePrivate or
    // Update. The selection key is plugin-side and is discovered rather than hardcoded.
    public static ObsSettings BuildDisplayCaptureSettings(ObsSource source, int displayIndex)
    {
        ArgumentNullException.ThrowIfNull(source);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, source.EnumerateProperties(), displayIndex);
        return settings;
    }

    // As BuildDisplayCaptureSettings, for a source created with a known type id but not yet
    // instantiated, for the shape "discover what I will create, then create it with the discovered
    // settings". Pass the *created* source instead wherever there is one: this route reaches
    // obs_get_source_properties, which segfaults for both of linux-capture's capture types because
    // their property builders dereference the instance libobs has not made yet.
    public static ObsSettings BuildDisplayCaptureSettings(string typeId, int displayIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, ObsSourceProperties.EnumerateTypeProperties(typeId), displayIndex);
        return settings;
    }

    // The display-selection property a screen-capture source declares, and the acceptable display
    // indices. This is the discovery surface that tells a recorder whether a display index it has
    // in mind is one the plugin will accept.
    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        return FindDisplaySelection(ObsSourceProperties.EnumerateTypeProperties(typeId))?.Items ?? [];
    }

    // The instance route for the display choices, which capture sources need because the type-level
    // probe can crash before the source exists (the property builder needs the source's connection).
    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return FindDisplaySelection(source.EnumerateProperties())?.Items ?? [];
    }

    // ---- monitor selection ----

    // The monitors this display-capture source will accept, in the plugin's own order. Through the
    // instance, because the type-level property probe can crash for a capture source.
    public static IReadOnlyList<ObsDisplay> EnumerateDisplays(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return DescribeDisplays(source.EnumerateProperties());
    }

    // As EnumerateDisplays, for a type id — the "what would I get if I created this" route.
    public static IReadOnlyList<ObsDisplay> EnumerateDisplays(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);
        return DescribeDisplays(ObsSourceProperties.EnumerateTypeProperties(typeId));
    }

    // The display-selection property's items read as monitors. Public and pure so the selection
    // rules are testable against a synthetic property list rather than a machine's real monitors.
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

            // A String list carries the opaque device id the plugin matches on; an Int list carries
            // the index, which is then the whole identity a screen has.
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

        // The primary monitor is the one at the virtual desktop's origin — the platform fact both
        // plugins' labels expose. A list whose labels carry no position falls back to the first.
        if (displays.Count == 0)
            return displays;

        var primary = origin < 0 ? 0 : origin;
        displays[primary] = displays[primary] with { Primary = true };
        return displays;
    }

    // The monitor to capture, given the saved preference. A preference that names a monitor which is
    // not attached is NOT an error and is never rewritten: this session falls back to the primary
    // (or first) monitor and says so, so replugging the monitor restores the user's choice.
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

    // The settings that capture the given monitor, as a fresh object to pass to Update.
    public static ObsSettings BuildDisplayCaptureSettings(ObsSource source, ObsDisplay display)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(display);

        var settings = new ObsSettings();
        ApplyDisplay(settings, source.EnumerateProperties(), display);
        return settings;
    }

    // ---- discovery ----

    // The source-type id the platform's game-capture plugin registers, or null when none is
    // present. Windows: "game_capture".
    private static IReadOnlyList<ObsSourceProperty> DiscoverGameCaptureProperties() =>
        ObsSourceProperties.EnumerateTypeProperties("game_capture");

    private static bool ApplyTarget(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, ObsGameCaptureTarget target)
    {
        if (target.IsEmpty)
            return false;

        var wrote = false;

        // Modern win-capture reads the target from this one string and nothing else — the separate
        // title/class/exe keys older builds carried are gone, and writing them now would only risk
        // landing on some unrelated property.
        if (FindKey(properties, WindowKey) is { } windowKey)
        {
            settings.SetString(windowKey, BuildWindowMatchString(target));
            wrote = true;
        }

        // Forced rather than left to the plugin default, because the default is only right for the
        // exe-only case and a settings object that says what it means survives an obs_source_update
        // merge unchanged.
        if (FindKey(properties, PriorityKey) is { } priorityKey)
        {
            settings.SetInt(priorityKey, (int)ResolveWindowPriority(target));
            wrote = true;
        }

        return wrote;
    }

    // The display-selection property, by the key each platform's plugin declares: xshm_input's
    // "screen", monitor_capture's "monitor_id". A name merely *containing* "screen" is the last
    // resort, not the rule — substring matching is how a write lands on the wrong property.
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

    // How the index is written depends on what the list carries. An Int list (xshm_input's
    // "screen") uses the display index as the value itself.
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

    // As ApplyDisplayIndex, but writing an identity rather than a position: on a String list the
    // saved device id goes in as-is, so a monitor that changed places in the list is still the one
    // captured.
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

    // Matches a declared property name exactly. The earlier substring form would bind a key to the
    // first property that merely mentioned it, and writing a string into, say, a bool property is
    // silent — the settings object takes it and the plugin reads a default.
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
