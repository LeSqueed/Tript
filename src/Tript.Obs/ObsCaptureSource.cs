// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// Which picture a capture source should supply. Game capture is a Windows capability: libobs's
// linux-capture module has no game-capture source, so on Linux the alpha recorder falls back to
// display capture and the game path is absent (spec/recorder.md, "Capture"). The capture-source
// ids and their settings keys are plugin-side and discovered at runtime through the type
// properties — nothing here hardcodes them.
public enum ObsCaptureMode
{
    // A specific game process, via the platform's game-capture source. Windows only; there is no
    // Linux equivalent in libobs.
    Game,
    // A display or monitor, via the platform's screen-capture source (xshm_input on Linux,
    // monitor_capture on Windows).
    Display
}

// A target for game capture, carried as the capture source's own vocabulary so the caller never
// touches a plugin key. What a process identity must contain is exactly what the discovered
// properties accept: on Windows the game-capture plugin matches on a window by title or class and
// on an executable by name. Which of the two the recorder can fill in depends on what its process
// watcher knows (spec/recorder.md, "Auto-start on game detection"); the alpha watcher knows the
// executable name, so that is what the recorder attaches by.
public sealed record ObsGameCaptureTarget(string? WindowTitle, string? WindowClass, string? ExecutablePath)
{
    public bool IsEmpty => WindowTitle is null && WindowClass is null && ExecutablePath is null;
}

// The capture-source surface: creating the platform's game or display capture source, attaching it
// to a detected game, and re-targeting an existing source without restarting it (spec/recorder.md,
// "Sources" — the target window can change during a session as the game re-creates its window, so
// the recorder updates rather than restarts).
//
// Deliberately not a factory with a mode→id table: which capture source to use is a per-platform
// and per-configuration decision that the recorder's orchestration makes. What this surface owns
// is the *discovered* settings — the keys come from the source's own properties rather than from
// a hardcoded table, because the capture keys are plugin-side and not in the libobs headers
// (spec/obs-binding.md, "Capture sources" and Part 11). The caller creates the source, and this
// type tells it which of the discovered keys mean "the target".
public static class ObsCaptureSource
{
    // ---- game-capture attachment ----

    // The settings keys game capture reads to attach to a window. Discovered from the source's own
    // properties rather than hardcoded: the plugin-side keys are not in the libobs headers, and
    // obs_get_source_properties is the discovery route the spec names (Part 11). Each property's
    // list items carry the accepted values, so a caller can also enumerate the windows/processes
    // the plugin offers instead of supplying a handle itself.
    //
    // Null when the id that was probed is not a game-capture source — on Linux there is no
    // game-capture source at all (spec/recorder.md, "Capture"), so the fallback is display capture
    // and this returns null to say so.
    public static IReadOnlyList<ObsSourceProperty>? GetGameCaptureProperties()
    {
        var properties = DiscoverGameCaptureProperties();
        return properties.Count == 0 ? null : properties;
    }

    // The settings that attach a game-capture source to the given target, as a fresh object to pass
    // to CreatePrivate or Update. The keys come from the discovered properties: only a property
    // game capture declares is written, and the window key is preferred over the process key
    // because it is the more precise handle — the window the game actually presents. On Linux there
    // is no game-capture source, so this returns null and the recorder falls back to display
    // capture. Returns an object carrying no keys when the target is empty or no matching property
    // is declared, which the caller can still pass harmlessly.
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

    // Applies the target to an existing game-capture source without restarting it — the re-targeting
    // seam. The recorder calls this when the detected game re-creates its window mid-session
    // (spec/recorder.md, "Sources"). Returns false when the source cannot be re-targeted, which on
    // Linux is every source (no game capture) and on Windows is a source whose type does not declare
    // the window/process properties.
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

    // ---- display capture settings ----

    // The settings that capture the given display, as a fresh object to pass to CreatePrivate.
    // The display index is written to the screen-selection key the source declares — xshm_input's
    // display-index list on Linux, monitor_capture's monitor list on Windows — discovered rather
    // than hardcoded, because the key is plugin-side (Part 11). The property list is read from the
    // *instance* (ObsSource.EnumerateProperties), which for capture sources is the reliable route:
    // the instance holds the connection the property builder needs (measured on linux-capture
    // 32.2.1, where the type-level probe crashes on an Xwayland server). When the source declares
    // no display-selection property (it may offer no choices at all), a bare settings object is
    // returned so the source can still be created with its plugin defaults.
    public static ObsSettings BuildDisplayCaptureSettings(ObsSource source, int displayIndex)
    {
        ArgumentNullException.ThrowIfNull(source);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, source.EnumerateProperties(), displayIndex);
        return settings;
    }

    // As BuildDisplayCaptureSettings, for a source created with a known type id but not yet
    // instantiated. Passing the *created* source is preferred — it takes the reliable instance
    // route — but this overload exists for the shape "discover what I will create, then create it
    // with the discovered settings".
    public static ObsSettings BuildDisplayCaptureSettings(string typeId, int displayIndex)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);

        var settings = new ObsSettings();
        ApplyDisplayIndex(settings, ObsSourceProperties.EnumerateTypeProperties(typeId), displayIndex);
        return settings;
    }

    // The display-selection property a screen-capture source declares, and the acceptable display
    // indices. This is the discovery surface that tells a recorder whether a display index it has
    // in mind is one the plugin will accept. Empty when the source offers no display choices. The
    // instance route is preferred for the same reason as BuildDisplayCaptureSettings; the
    // type-level overload exists for the pre-creation shape.
    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(string typeId)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeId);

        var displayProperty = ObsSourceProperties.EnumerateTypeProperties(typeId)
            .FirstOrDefault(property =>
                property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase));

        return displayProperty.Name is null ? [] : displayProperty.Items;
    }

    // The instance route for the display choices, which capture sources need because the type-level
    // probe can crash before the source exists (the property builder needs the source's connection).
    public static IReadOnlyList<ObsSourcePropertyItem> EnumerateDisplayChoices(ObsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var displayProperty = source.EnumerateProperties()
            .FirstOrDefault(property =>
                property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase));

        return displayProperty.Name is null ? [] : displayProperty.Items;
    }

    // ---- discovery ----

    // The source-type id the platform's game-capture plugin registers, or null when none is
    // present. Windows: "game_capture". Linux: no game-capture source exists in libobs
    // (spec/recorder.md, "Capture"), so this is null and the recorder falls back to display
    // capture for the alpha.
    private static IReadOnlyList<ObsSourceProperty> DiscoverGameCaptureProperties() =>
        ObsSourceProperties.EnumerateTypeProperties("game_capture");

    private static bool ApplyTarget(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, ObsGameCaptureTarget target)
    {
        var wrote = false;

        // The window key is the more precise handle — it is the window the game actually presents —
        // so it is written when the target has one. A null value erases the key rather than writing
        // "null", which is what libobs does with a NULL char*.
        if (target.WindowTitle is not null &&
            FindKey(properties, "window", "title", "capture_window") is { } windowTitleKey)
        {
            settings.SetString(windowTitleKey, target.WindowTitle);
            wrote = true;
        }
        else if (target.WindowClass is not null &&
                 FindKey(properties, "window", "class") is { } windowClassKey)
        {
            settings.SetString(windowClassKey, target.WindowClass);
            wrote = true;
        }

        if (target.ExecutablePath is not null &&
            FindKey(properties, "exe", "executable", "process") is { } exeKey)
        {
            settings.SetString(exeKey, target.ExecutablePath);
            wrote = true;
        }

        return wrote;
    }

    // The screen-selection key of a display-capture source. xshm_input on Linux declares a list of
    // displays ("screen"); the write is conditional on the property existing, so a source that
    // offers no choices is created with its defaults instead of a key it would ignore.
    private static void ApplyDisplayIndex(ObsSettings settings, IReadOnlyList<ObsSourceProperty> properties, int displayIndex)
    {
        foreach (var property in properties)
        {
            if (property.Type == ObsPropertyType.List &&
                property.Name.Contains("screen", StringComparison.OrdinalIgnoreCase))
            {
                settings.SetInt(property.Name, displayIndex);
                return;
            }
        }
    }

    private static string? FindKey(IReadOnlyList<ObsSourceProperty> properties, params string[] fragments)
    {
        foreach (var property in properties)
        {
            foreach (var fragment in fragments)
            {
                if (property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return property.Name;
            }
        }

        return null;
    }
}
