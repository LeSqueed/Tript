// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class OutputSettingsKeyTests
{
    private const string FfmpegMuxerId = "ffmpeg_muxer";

    public static readonly IReadOnlyList<OutputSettingsKey> SpecifiedKeys = new[]
    {
        new OutputSettingsKey("path", ObsSettingsValueType.String, Pinned.OutputProperty),
        new OutputSettingsKey("muxer_settings", ObsSettingsValueType.String, Pinned.PluginBehavior)
    };

    [SkippableFact]
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

            if (!settings.HasUserValue(key.Key))
                failures.Add($"{key.Key}: written but reports no user value");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [SkippableFact]
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

    [SkippableFact]
    public void EveryOutputPropertyPinnedKey_IsPresentInTheLivePropertySurface()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var live = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId).Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in SpecifiedKeys.Where(key => key.Pinned == Pinned.OutputProperty))
            Assert.True(live.Contains(key.Key), $"'{key.Key}' is not in the live property list ({string.Join(", ", live)}).");
    }

    [SkippableFact]
    public void TheLivePropertySurface_IsThePinnedOneOnThisOBS()
    {
        using var session = ObsSession.StartWithSourceTypes();

        var live = ObsOutput.EnumerateTypeProperties(FfmpegMuxerId).Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(["path"], live);
    }

    [SkippableFact]
    public void TheLiveDefaults_AreEmptyOnThisOBS()
    {
        using var session = ObsSession.StartWithSourceTypes();

        using var defaults = ObsOutput.GetTypeDefaults(FfmpegMuxerId);

        Assert.NotNull(defaults);
        Assert.Empty(defaults.EnumerateEntries());
    }

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
        OutputProperty,
        PluginBehavior
    }
}
