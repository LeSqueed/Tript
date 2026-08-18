// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs.Interop;

namespace Tript.Obs;

// libobs's obs_source_t: an input, a filter, a transition or a scene. Tript creates inputs — a
// capture source per platform — and reaches scenes through ObsScene.
//
// Three measured properties of the native object shape this class, none of them stated in the
// headers:
//
//   * Creation copies the id and the name. Neither buffer has to outlive the call, which is the
//     opposite of obs_reset_video's graphics module name and worth stating because the two calls
//     look alike.
//   * The settings object is *shared*, not copied: obs_source_get_settings hands back the very
//     obs_data_t the source was created with, and a later edit through the caller's own reference is
//     visible to the source. See Create.
//   * obs_shutdown destroys every source regardless of outstanding references, so a handle that
//     outlives the context has nothing to release — hence ObsSourceHandle being context-owned.
//
// Not thread-safe as a wrapper. libobs guards the source's own state; a read-modify-write through
// this class is not atomic.
public sealed class ObsSource : IDisposable
{
    private readonly ObsSourceHandle _handle;

    private ObsSource(nint pointer) => _handle = new ObsSourceHandle(pointer);

    // For pointers libobs hands over already incremented — obs_get_source_by_name, obs_source_get_ref
    // and the scene navigation calls. The reference becomes this object's to release.
    internal static ObsSource FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null source where one was expected.");

        return new ObsSource(pointer);
    }

    internal static ObsSource? FromOwnedPointerOrNull(nint pointer) =>
        pointer == nint.Zero ? null : new ObsSource(pointer);

    internal nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    // ---- creation ----

    // Creates a source of a registered type. The settings object, if given, is *retained* rather
    // than copied: the source and the caller end up sharing it, and disposing the caller's
    // reference afterwards is safe only because libobs takes one of its own.
    public static ObsSource Create(string id, string name, ObsSettings? settings = null) =>
        Create(id, name, settings, findableByName: true);

    // As Create, but the source is not added to the core's list: it cannot be found by name, does not
    // appear in an enumeration, and is not saved. What a recorder wants for a source it owns
    // outright.
    public static ObsSource CreatePrivate(string id, string name, ObsSettings? settings = null) =>
        Create(id, name, settings, findableByName: false);

    private static ObsSource Create(string id, string name, ObsSettings? settings, bool findableByName)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // Null is not merely unsupported here: obs_source_create dereferences the id without
        // checking it, so a null crashes the process rather than returning null.
        if (GetTypeDisplayName(id) is null)
            throw new ObsException(
                $"No loaded module registers a source type with the id '{id}'. " +
                "libobs would answer this with a placeholder source that renders nothing.");

        var pointer = findableByName
            ? ObsNative.obs_source_create(id, name, settings?.Pointer ?? nint.Zero, nint.Zero)
            : ObsNative.obs_source_create_private(id, name, settings?.Pointer ?? nint.Zero);

        if (pointer == nint.Zero)
            throw new ObsException(
                $"obs_source_create returned null for '{id}'. It reports no reason; the log handler is where one would appear.");

        return new ObsSource(pointer);
    }

    // The translated name of a source *type*, and the only reliable test of whether a type is
    // registered at all: null means no loaded module provides it.
    public static string? GetTypeDisplayName(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_display_name(id));
    }

    public static ObsSourceOutputFlags GetTypeOutputFlags(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return (ObsSourceOutputFlags)ObsNative.obs_get_source_output_flags(id);
    }

    // Null when no source of that name exists. Only finds sources created findable; the reference is
    // incremented, so the result is the caller's to dispose.
    public static ObsSource? FindByName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FromOwnedPointerOrNull(ObsNative.obs_get_source_by_name(name));
    }

    // A second owning reference to the same source, for handing one to code with a lifetime of its
    // own. Both have to be disposed.
    public ObsSource AddReference() => FromOwnedPointer(ObsNative.obs_source_get_ref(Pointer));

    // A reference that does not keep the source alive, and the only way to observe that it has been
    // destroyed. The weak reference itself must still be disposed — it outlives the OBS context.
    public ObsWeakSource CreateWeakReference() =>
        ObsWeakSource.FromOwnedPointer(ObsNative.obs_source_get_weak_source(Pointer));

    public void Dispose() => _handle.Dispose();

    // ---- identity ----

    // The registered type id, e.g. "xshm_input". Versioned ids report the version here and the plain
    // name through UnversionedId.
    public string Id => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_id(Pointer)) ?? string.Empty;

    public string UnversionedId =>
        Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_unversioned_id(Pointer)) ?? string.Empty;

    // Not unique: libobs accepts two sources with the same name and a lookup finds the first.
    public string Name
    {
        get => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_name(Pointer)) ?? string.Empty;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            ObsNative.obs_source_set_name(Pointer, value);
        }
    }

    // Assigned by libobs at creation and stable for the source's life, which is what a name is not.
    public string Uuid => Utf8Marshal.ReadBorrowed(ObsNative.obs_source_get_uuid(Pointer)) ?? string.Empty;

    public ObsSourceType Type => (ObsSourceType)ObsNative.obs_source_get_type(Pointer);

    public ObsSourceOutputFlags OutputFlags => (ObsSourceOutputFlags)ObsNative.obs_source_get_output_flags(Pointer);

    public bool IsScene => ObsNative.obs_source_is_scene(Pointer);

    // ---- settings ----

    // The source's live settings object, with its reference incremented — the caller disposes it.
    // Editing it does not by itself reconfigure the source; Update is what tells the source to read
    // its settings again.
    public ObsSettings GetSettings() => ObsSettings.FromOwnedPointer(ObsNative.obs_source_get_settings(Pointer));

    // The properties this *instance* declares, read through obs_source_properties rather than the
    // type-level obs_get_source_properties. For most source types the two agree; for capture
    // sources the instance route is the reliable one, because the instance already holds the
    // connection the property builder needs — measured on linux-capture 32.2.1, where the type-
    // level probe for xshm_input crashes on an Xwayland server while this succeeds.
    public IReadOnlyList<ObsSourceProperty> EnumerateProperties() =>
        ObsSourceProperties.EnumerateProperties(ObsNative.obs_source_properties(Pointer));

    // Applies the given keys over the source's existing settings and asks it to reconfigure.
    public void Update(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_source_update(Pointer, settings.Pointer);
    }

    // Replaces the settings outright rather than merging, and does *not* ask the source to
    // reconfigure — the pair to use when restoring a saved configuration before the source is live.
    public void ResetSettings(ObsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObsNative.obs_source_reset_settings(Pointer, settings.Pointer);
    }

    // ---- state ----

    // The size the source renders at, which for a capture source is only known once it has attached
    // to something. Zero until then rather than an error.
    public uint Width => ObsNative.obs_source_get_width(Pointer);

    public uint Height => ObsNative.obs_source_get_height(Pointer);

    // Before filters. Differs from Width once a filter that resizes is in the chain.
    public uint BaseWidth => ObsNative.obs_source_get_base_width(Pointer);

    public uint BaseHeight => ObsNative.obs_source_get_base_height(Pointer);

    public bool IsEnabled
    {
        get => ObsNative.obs_source_enabled(Pointer);
        set => ObsNative.obs_source_set_enabled(Pointer, value);
    }

    // True while the source is part of what is being output. Showing is the weaker claim: on screen
    // somewhere, output or preview.
    public bool IsActive => ObsNative.obs_source_active(Pointer);

    public bool IsShowing => ObsNative.obs_source_showing(Pointer);

    // ---- audio ----

    // Which of libobs's audio mixers this source feeds, as a bitmask — bit n means mixer n, and
    // MAX_AUDIO_MIXES is 6. The multi-track
    // routing gives every source on a track the same single bit, so a track carries every source
    // sharing its mixer.
    public uint AudioMixers
    {
        get => ObsNative.obs_source_get_audio_mixers(Pointer);
        set => ObsNative.obs_source_set_audio_mixers(Pointer, value);
    }

    // The per-source gain, a linear multiplier. Volume is per-source, not per-track: two sources
    // merged into one track keep their own volumes. The
    // getter reads back what the setter stored.
    public float Volume
    {
        get => ObsNative.obs_source_get_volume(Pointer);
        set => ObsNative.obs_source_set_volume(Pointer, value);
    }

    // The active pair is what makes a capture source actually produce audio: libobs runs a source's
    // audio only while its active reference count is non-zero, and a freshly created source has a
    // count of zero. A recorder marks each source it routes active at start and inactive at stop;
    // the counter is balanced, so every MarkActive needs a matching MarkInactive.
    public void MarkActive() => ObsNative.obs_source_inc_active(Pointer);

    public void MarkInactive() => ObsNative.obs_source_dec_active(Pointer);

    // ---- removal ----

    // Marks the source removed and signals every holder to let go. It does not destroy anything by
    // itself, and for an input source it is not needed at all — a plain release destroys one. It is
    // how a *scene* stops being held by the OBS core; see ObsScene.
    public void MarkRemoved() => ObsNative.obs_source_remove(Pointer);

    public bool IsRemoved => ObsNative.obs_source_removed(Pointer);
}

// A reference that does not keep its source alive. Its control block is a bmem allocation that
// survives obs_shutdown — measured — so it is released like a settings object rather than like the
// source it points at.
public sealed class ObsWeakSource : IDisposable
{
    private readonly ObsWeakSourceHandle _handle;

    private ObsWeakSource(nint pointer) => _handle = new ObsWeakSourceHandle(pointer);

    internal static ObsWeakSource FromOwnedPointer(nint pointer)
    {
        if (pointer == nint.Zero)
            throw new ObsException("libobs returned a null weak source where one was expected.");

        return new ObsWeakSource(pointer);
    }

    private nint Pointer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed || _handle.IsInvalid, this);
            return _handle.DangerousGetHandle();
        }
    }

    // True once the source has been destroyed. Release only schedules the destruction, so a caller
    // asserting on this has to drain the destroy queue first — ObsRuntime.WaitForDestroyQueue.
    public bool IsExpired => ObsNative.obs_weak_source_expired(Pointer);

    // An owning reference again, or null if the source is gone. The only safe way back to a source
    // held weakly: checking IsExpired first and dereferencing afterwards is a race.
    public ObsSource? TryGetSource() => ObsSource.FromOwnedPointerOrNull(ObsNative.obs_weak_source_get_source(Pointer));

    public void Dispose() => _handle.Dispose();
}
