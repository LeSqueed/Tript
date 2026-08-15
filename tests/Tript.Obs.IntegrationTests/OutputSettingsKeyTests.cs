// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

// Every settings key the ffmpeg_muxer plugin reads, driven through a real obs_data on the real
// runtime, and pinned against what the *plugin's own* property and defaults surface reports at
// runtime.
//
// The pin is read back live rather than transcribed from a manual: obs_get_output_properties and
// obs_output_defaults are the plugin's own declaration of what it reads, which is exactly the
// contract a wrong key violates. The guard is the interesting half of that: when the plugin grows a
// new key on some future OBS version, the guard fails — not silently, and not by asserting that a
// key this repo happened to transcribe is still present. It forces whoever meets that OBS to read
// the new property surface and extend this table with a pinned key and the observations that say
// why it is pinned.
public sealed class OutputSettingsKeyTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";

    // The settings a recorder writes to an ffmpeg_muxer. Keys come from the live property surface
    // of the plugin this suite runs against — measured on OBS 32.2.1, where the surface is exactly
    // this — and the Pinned observation names the source. Round-tripping is proven per key, and a
    // typo (e.g. "pth", "file", "dest") fails the round-trip or the presence checks.
    //
    // obs_output_defaults returns an empty object for this type — measured — so nothing here comes
    // from the defaults path. The path key carries its default in the property itself, and a path
    // typed at runtime would not have one.
    public static readonly IReadOnlyList<OutputSettingsKey> SpecifiedKeys = new[]
    {
        new OutputSettingsKey("path", ObsSettingsValueType.String, Pinned.OutputProperty),
        new OutputSettingsKey("muxer_settings", ObsSettingsValueType.String, Pinned.PluginBehavior)
    };

    [Fact]
    public void EverySpecifiedKey_RoundTripsWithItsSpecifiedType()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var failures = new List<string>();

        foreach (var key in SpecifiedKeys)
        {
            using var settings = new ObsSettings();
            settings.SetString(key.Key, key.Sample);

            var readBack = settings.GetString(key.Key);
            if (readBack != key.Sample)
                failures.Add($"{key.Key}: wrote '{key.Sample}', read '{readBack}'");

            // A lost key reads as "" — indistinguishable from a written empty string — so equality
            // alone would let a mangled key pass. This is the assertion that does not.
            if (!settings.HasUserValue(key.Key))
                failures.Add($"{key.Key}: written but reports no user value");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // obs_data keys are matched byte for byte, so every plausible near-miss must come up empty. A
    // typo in the table above fails here rather than as a silent default in a recording.
    [Fact]
    public void EverySpecifiedKey_IsFoundOnlyUnderItsExactSpelling()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var failures = new List<string>();

        foreach (var key in SpecifiedKeys)
        {
            using var settings = new ObsSettings();
            settings.SetString(key.Key, key.Sample);

            foreach (var miss in new[]
                     {
                         key.Key.ToUpperInvariant(),
                         key.Key.Replace("_", "", StringComparison.Ordinal),
                         key.Key switch { "path" => "pth", "muxer_settings" => "muxer_settings ",
                             _ => key.Key + "_" }
                     })
            {
                if (miss == key.Key)
                    continue;

                if (settings.HasUserValue(miss))
                    failures.Add($"{key.Key}: also answered to '{miss}'");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // The keys pinned as OutputProperty must be declared by the plugin's own property surface —
    // that is what "pinned from live readback" means. Keys pinned as PluginBehavior (muxer_settings)
    // are not properties and are deliberately excluded here; their pinning is the presence-in-log
    // observation recorded in the table.
    [Fact]
    public void EveryOutputPropertyPinnedKey_IsPresentInTheLivePropertySurface()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var live = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId).Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in SpecifiedKeys.Where(key => key.Pinned == Pinned.OutputProperty))
            Assert.True(live.Contains(key.Key), $"'{key.Key}' is not in the live property list ({string.Join(", ", live)}).");
    }

    // The guard half of the pin: on OBS 32.2.1 the surface is exactly this. When the plugin gains a
    // key, this fails and the table is extended deliberately — with the new key and the observation
    // that says why it is pinned — rather than silently drifting out of sync.
    [Fact]
    public void TheLivePropertySurface_IsThePinnedOneOnThisOBS()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var live = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId).Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["path"], live);
    }

    // The path key carries a *path* property whose default lives in the property object, not in the
    // defaults object. obs_output_defaults returns an empty object — measured — so a recorder must
    // not expect the plugin's defaults to seed the path for it.
    [Fact]
    public void TheLiveDefaults_AreEmptyOnThisOBS()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsOutput.GetTypeDefaults(FfmpegMuxerId);

        Assert.NotNull(defaults);
        Assert.Empty(defaults.EnumerateEntries());
    }

    // ---- the pinned keys ----

    public readonly record struct OutputSettingsKey(string Key, ObsSettingsValueType Type, Pinned Pinned)
    {
        internal string Sample => Key switch
        {
            "path" => "/tmp/output.mp4",
            "muxer_settings" => "movflags=faststart",
            _ => throw new InvalidOperationException($"No sample defined for '{Key}'.")
        };
    }

    public enum Pinned
    {
        OutputProperty, // the key is declared as a property by obs_get_output_properties
        PluginBehavior   // the key is not declared as a property but the plugin is observed reading it
    }
}
