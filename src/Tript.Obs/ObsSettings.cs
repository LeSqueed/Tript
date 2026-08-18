// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_data_t: the JSON-shaped key/value bag that every source, encoder and output is
// configured through. Not to be confused with ObsVideoSettings and ObsAudioSettings, which mirror
// two fixed native structs; this one is open-ended and its keys are plugin-private strings.
//
// Three properties of the native object drive the whole shape of this class, and each was measured
// on 32.2.1 rather than read from a header:
//
//   * It is independent of the OBS context. Usable before obs_startup, and it survives
//     obs_shutdown — hence ObsSettingsHandle rather than ObsContextHandle.
//   * It copies every string it is given, key and value alike. Nothing has to be kept alive for
//     the object's lifetime, which is the opposite of obs_reset_video's graphics module name.
//   * A key holds one type, and writing a different type to it discards the value that was there
//     *including its default*. See SetInt and the note on defaults below.
//
// Not thread-safe. libobs guards obs_data with its own mutex, but a get-then-set pair through this
// class is not atomic and callers configuring one object from several threads must say so
// themselves.
public sealed class ObsSettings : IDisposable
{
    private readonly ObsSettingsHandle _handle;

    public ObsSettings() : this(Create())
    {
    }

    private ObsSettings(nint pointer) => _handle = new ObsSettingsHandle(pointer);

    // For pointers libobs hands over already incremented — obs_data_get_obj, obs_data_get_defaults,
    // and later obs_encoder_get_settings. The reference becomes this object's to release.
    internal static ObsSettings FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null settings object where one was expected.");

        return new ObsSettings(pointer);
    }

    internal static ObsSettings? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsSettings(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    // ---- creation ----

    // Returns null rather than throwing when the text is not JSON: libobs logs the parser's own
    // message through the log handler, which says far more about what is wrong than a rethrow can,
    // and a malformed configuration file is an expected condition rather than a fault.
    public static ObsSettings? FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json(json));
    }

    public static ObsSettings? FromJsonFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json_file(path));
    }

    // Falls back to "<path><backupExtension>" when the file is missing or unparseable. The
    // extension is expected to include its own separator, as libobs concatenates it unchanged.
    public static ObsSettings? FromJsonFile(string path, string backupExtension)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(backupExtension);
        return FromOwnedPointerOrNull(ObsNative.obs_data_create_from_json_file_safe(path, backupExtension));
    }

    public void Dispose() => _handle.Dispose();

    // ---- typed writes ----

    // A null value is accepted and stores an empty string, which is what libobs does with a NULL
    // char* — measured, and worth knowing because it also marks the key as having a user value.
    public void SetString(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_string(Pointer, name, value);
    }

    // long, not int: libobs stores every number as long long and every settings key in the encoder
    // tables goes through this one call.
    public void SetInt(string name, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_int(Pointer, name, value);
    }

    public void SetDouble(string name, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_double(Pointer, name, value);
    }

    public void SetBool(string name, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_bool(Pointer, name, value);
    }

    // Stored by reference, not copied: libobs takes its own reference and later edits made through
    // the caller's object are visible here. Passing null stores a JSON null — but only from OBS
    // 32.1.0, which added the null check obs_data_to_json needs to walk one; below that line the
    // entry is a landmine that segfaults the process on the next ToJson of the *parent*. Erase or
    // UnsetUserValue is how to say "not configured" without one.
    public void SetObject(string name, ObsSettings? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_obj(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    // Also by reference. Passing null stores an empty array rather than a JSON null — the
    // asymmetry with SetObject is libobs's, measured on 32.2.1.
    public void SetArray(string name, ObsSettingsArray? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_array(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    // ---- typed reads ----

    // Never null. An absent key, and a key holding any non-string type, both read as empty — libobs
    // returns a pointer to a static empty string in every one of those cases, so emptiness cannot
    // distinguish "unset" from "set to nothing". HasUserValue is the question to ask instead.
    public string GetString(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_data_get_string(Pointer, name)) ?? string.Empty;
    }

    // Zero for an absent key and for a key holding a string or a bool. A double truncates toward
    // zero rather than rounding.
    public long GetInt(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_int(Pointer, name);
    }

    public double GetDouble(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_double(Pointer, name);
    }

    // False for an absent key and for every non-bool type, including the integer 1. libobs performs
    // no conversion into or out of booleans at all.
    public bool GetBool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_bool(Pointer, name);
    }

    // The caller owns the returned object and must dispose it; libobs increments the reference
    // before handing it back. Null when the key is absent or holds something other than an object.
    public ObsSettings? GetObject(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_data_get_obj(Pointer, name));
    }

    // Owned by the caller, as GetObject.
    public ObsSettingsArray? GetArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsSettingsArray.FromOwnedPointerOrNull(ObsNative.obs_data_get_array(Pointer, name));
    }

    // ---- defaults ----
    //
    // A default and a user value live in the same entry, so they share its type. Writing a user
    // value of a *different* type to a key that has a default leaves the default reported as
    // present but unreadable through either accessor, and unsetting the user value does not bring
    // it back. Setting the right type is therefore not a matter of tidiness: the wrong one destroys
    // the value the plugin would otherwise have fallen back on.

    public void SetDefaultString(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_string(Pointer, name, value);
    }

    public void SetDefaultInt(string name, long value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_int(Pointer, name, value);
    }

    public void SetDefaultDouble(string name, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_double(Pointer, name, value);
    }

    public void SetDefaultBool(string name, bool value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_bool(Pointer, name, value);
    }

    public void SetDefaultObject(string name, ObsSettings? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_obj(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public void SetDefaultArray(string name, ObsSettingsArray? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_set_default_array(Pointer, name, value?.Pointer ?? nint.Zero);
    }

    public string GetDefaultString(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_data_get_default_string(Pointer, name)) ?? string.Empty;
    }

    public long GetDefaultInt(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_int(Pointer, name);
    }

    public double GetDefaultDouble(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_double(Pointer, name);
    }

    public bool GetDefaultBool(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_get_default_bool(Pointer, name);
    }

    public ObsSettings? GetDefaultObject(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_data_get_default_obj(Pointer, name));
    }

    public ObsSettingsArray? GetDefaultArray(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsSettingsArray.FromOwnedPointerOrNull(ObsNative.obs_data_get_default_array(Pointer, name));
    }

    // Every default this object carries, as a new object in which they appear as user values —
    // which is what makes the result serialisable and comparable. The caller disposes it.
    public ObsSettings GetDefaults() => FromOwnedPointer(ObsNative.obs_data_get_defaults(Pointer));

    // ---- presence ----

    // "Was this configured?", which no getter can answer: an unset key and a key set to zero, false
    // or empty read identically.
    public bool HasUserValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_has_user_value(Pointer, name);
    }

    public bool HasDefaultValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return ObsNative.obs_data_has_default_value(Pointer, name);
    }

    // ---- removal and merging ----

    // Removes the entry outright, default included.
    public void Erase(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_erase(Pointer, name);
    }

    // Drops the user value and leaves the default in place, so the next read reports what the
    // plugin would have used. The counterpart to Erase, and the one that expresses "unconfigure".
    public void UnsetUserValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_unset_user_value(Pointer, name);
    }

    public void UnsetDefaultValue(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ObsNative.obs_data_unset_default_value(Pointer, name);
    }

    // Clears user values only. Defaults survive, which is what makes it safe to reuse an object
    // obtained from obs_encoder_defaults.
    public void Clear() => ObsNative.obs_data_clear(Pointer);

    // Copies the other object's entries over this one's. Nested objects are replaced whole rather
    // than merged recursively — a child present in both ends up as the other object's child, with
    // this object's keys inside it lost.
    public void Apply(ObsSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        ObsNative.obs_data_apply(Pointer, other.Pointer);
    }

    // ---- serialisation ----

    // The pointer libobs returns here is a cache on the object that the *next* json call on the
    // same object invalidates, so the string is copied into managed memory before returning. Two
    // such calls in one expression would otherwise leave the first result reading freed memory —
    // measured, and stated nowhere.
    public string ToJson(bool pretty = false, bool includeDefaults = false)
    {
        var pointer = (pretty, includeDefaults) switch
        {
            (false, false) => ObsNative.obs_data_get_json(Pointer),
            (false, true) => ObsNative.obs_data_get_json_with_defaults(Pointer),
            (true, false) => ObsNative.obs_data_get_json_pretty(Pointer),
            (true, true) => ObsNative.obs_data_get_json_pretty_with_defaults(Pointer)
        };

        return Utf8Marshal.ReadBorrowed(pointer) ?? string.Empty;
    }

    public bool SaveJson(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return ObsNative.obs_data_save_json(Pointer, path);
    }

    // Writes to "<path><tempExtension>", moves the previous file to "<path><backupExtension>", then
    // renames into place — so a crash mid-write leaves a readable file either way. Both extensions
    // are concatenated unchanged and are expected to carry their own separator.
    public bool SaveJson(string path, string tempExtension, string backupExtension, bool pretty = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(tempExtension);
        ArgumentNullException.ThrowIfNull(backupExtension);

        return pretty
            ? ObsNative.obs_data_save_json_pretty_safe(Pointer, path, tempExtension, backupExtension)
            : ObsNative.obs_data_save_json_safe(Pointer, path, tempExtension, backupExtension);
    }

    // ---- iteration ----

    // Materialised rather than lazy: each step holds a native item that has to be released, and an
    // iterator abandoned partway through — by a break, an exception or a caller that never enumerates
    // to the end — would leak it. Entries with only a default are included, which is how the key set
    // an encoder declares can be read off an object obtained from obs_encoder_defaults.
    public IReadOnlyList<ObsSettingsEntry> EnumerateEntries()
    {
        var entries = new List<ObsSettingsEntry>();
        var item = ObsNative.obs_data_first(Pointer);

        try
        {
            while (item != nint.Zero)
            {
                var name = Utf8Marshal.ReadBorrowed(ObsNative.obs_data_item_get_name(item));
                if (name is not null)
                    entries.Add(new ObsSettingsEntry(
                        name,
                        (ObsSettingsValueType)ObsNative.obs_data_item_gettype(item),
                        (ObsSettingsNumberType)ObsNative.obs_data_item_numtype(item),
                        ObsNative.obs_data_item_has_user_value(item),
                        ObsNative.obs_data_item_has_default_value(item)));

                // Releases the current item and overwrites it with the next, so there is exactly one
                // live item at a time and nothing to release once it returns false.
                if (!ObsNative.obs_data_item_next(ref item))
                    item = nint.Zero;
            }
        }
        finally
        {
            if (item != nint.Zero)
                ObsNative.obs_data_item_release(ref item);
        }

        return entries;
    }

    private static nint Create()
    {
        var pointer = ObsNative.obs_data_create();
        if (pointer == nint.Zero)
            throw new ObsException("obs_data_create returned null. It reports no reason; the log handler is where one would appear.");

        return pointer;
    }
}

// One entry as iteration sees it. The two flags are separate because both can be true at once, and
// which of them is set decides whether a read reflects a decision anyone made.
public readonly record struct ObsSettingsEntry(
    string Name,
    ObsSettingsValueType ValueType,
    ObsSettingsNumberType NumberType,
    bool HasUserValue,
    bool HasDefaultValue);
