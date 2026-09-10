// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class EncoderSettingsKeyTests
{
    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void EverySpecifiedKey_RoundTripsWithItsSpecifiedType(string family)
    {
        var keys = EncoderSettingsKeyTable.For(family);
        Assert.NotEmpty(keys);

        var failures = new List<string>();

        foreach (var key in keys)
        {
            using var settings = new ObsSettings();
            EncoderSettingsKeyTable.Write(settings, key, key.Sample);

            var readBack = EncoderSettingsKeyTable.Read(settings, key);
            if (!Equals(key.Sample, readBack))
                failures.Add($"{key.Key}: wrote {Describe(key.Sample)}, read {Describe(readBack)}");

            if (!settings.HasUserValue(key.Key))
                failures.Add($"{key.Key}: written but reports no user value");
        }

        Assert.True(failures.Count == 0, $"{family} — {string.Join("; ", failures)}");
    }

    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void EverySpecifiedDefault_IsReadBackWhenNoValueIsSet(string family)
    {
        var keys = EncoderSettingsKeyTable.For(family).Where(key => key.SpecifiedDefault is not null).ToArray();
        Assert.NotEmpty(keys);

        var failures = new List<string>();

        foreach (var key in keys)
        {
            using var settings = new ObsSettings();
            EncoderSettingsKeyTable.WriteDefault(settings, key, key.SpecifiedDefault!);

            if (!Equals(key.SpecifiedDefault, EncoderSettingsKeyTable.ReadDefault(settings, key)))
                failures.Add($"{key.Key}: default did not read back");

            if (!Equals(key.SpecifiedDefault, EncoderSettingsKeyTable.Read(settings, key)))
                failures.Add($"{key.Key}: plain read did not fall through to the default");

            if (!settings.HasDefaultValue(key.Key))
                failures.Add($"{key.Key}: default set but not reported");

            if (settings.HasUserValue(key.Key))
                failures.Add($"{key.Key}: a default alone was reported as a user value");
        }

        Assert.True(failures.Count == 0, $"{family} — {string.Join("; ", failures)}");
    }

    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void EveryAcceptedValue_SurvivesUnchangedIncludingItsCase(string family)
    {
        var keys = EncoderSettingsKeyTable.For(family).Where(key => key.AcceptedValues.Count > 0).ToArray();
        Assert.NotEmpty(keys);

        var failures = new List<string>();

        foreach (var key in keys)
        {
            foreach (var accepted in key.AcceptedValues)
            {
                using var settings = new ObsSettings();
                settings.SetString(key.Key, accepted);

                var readBack = settings.GetString(key.Key);
                if (!string.Equals(accepted, readBack, StringComparison.Ordinal))
                    failures.Add($"{key.Key}: wrote '{accepted}', read '{readBack}'");
            }
        }

        Assert.True(failures.Count == 0, $"{family} — {string.Join("; ", failures)}");
    }

    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void AWholeFamilysKeys_CoexistInOneSettingsObject(string family)
    {
        var keys = EncoderSettingsKeyTable.For(family);
        using var settings = new ObsSettings();

        foreach (var key in keys)
            EncoderSettingsKeyTable.Write(settings, key, key.Sample);

        var failures = keys
            .Where(key => !Equals(key.Sample, EncoderSettingsKeyTable.Read(settings, key)))
            .Select(key => $"{key.Key}: expected {Describe(key.Sample)}, got {Describe(EncoderSettingsKeyTable.Read(settings, key))}")
            .ToArray();

        Assert.True(failures.Length == 0, $"{family} — {string.Join("; ", failures)}");

        var written = settings.EnumerateEntries().Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(keys.Select(key => key.Key).ToHashSet(StringComparer.Ordinal), written);
    }

    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void EveryKey_ReadAsTheWrongType_YieldsNothingRatherThanTheValue(string family)
    {
        var failures = new List<string>();

        foreach (var key in EncoderSettingsKeyTable.For(family))
        {
            using var settings = new ObsSettings();

            switch (key.Type)
            {
                case ObsSettingsValueType.String:
                    settings.SetString(key.Key, "42");
                    if (settings.GetInt(key.Key) != 0)
                        failures.Add($"{key.Key}: a string read as an integer was parsed");
                    if (settings.GetBool(key.Key))
                        failures.Add($"{key.Key}: a string read as a boolean was true");
                    break;

                case ObsSettingsValueType.Number:
                    settings.SetInt(key.Key, 42);
                    if (settings.GetString(key.Key).Length != 0)
                        failures.Add($"{key.Key}: an integer read as a string was formatted");
                    if (settings.GetBool(key.Key))
                        failures.Add($"{key.Key}: a non-zero integer read as a boolean was true");
                    break;

                case ObsSettingsValueType.Boolean:
                    settings.SetBool(key.Key, true);
                    if (settings.GetInt(key.Key) != 0)
                        failures.Add($"{key.Key}: a boolean read as an integer was non-zero");
                    if (settings.GetString(key.Key).Length != 0)
                        failures.Add($"{key.Key}: a boolean read as a string was formatted");
                    break;

                default:
                    failures.Add($"{key.Key}: has no settable type");
                    break;
            }
        }

        Assert.True(failures.Count == 0, $"{family} — {string.Join("; ", failures)}");
    }

    [Theory]
    [MemberData(nameof(EncoderSettingsKeyTable.FamilyNames), MemberType = typeof(EncoderSettingsKeyTable))]
    public void EveryKey_IsFoundOnlyUnderItsExactSpelling(string family)
    {
        var failures = new List<string>();

        foreach (var key in EncoderSettingsKeyTable.For(family))
        {
            using var settings = new ObsSettings();
            EncoderSettingsKeyTable.Write(settings, key, key.Sample);

            foreach (var miss in new[] { key.Key.ToUpperInvariant(), key.Key + "_", key.Key.Replace("_", "", StringComparison.Ordinal) })
            {
                if (string.Equals(miss, key.Key, StringComparison.Ordinal))
                    continue;

                if (settings.HasUserValue(miss))
                    failures.Add($"{key.Key}: also answered to '{miss}'");
            }
        }

        Assert.True(failures.Count == 0, $"{family} — {string.Join("; ", failures)}");
    }

    [Fact]
    public void TheKeyTable_CoversEveryFamilyWithEnoughKeysToBeMeaningful()
    {
        Assert.Equal(5, EncoderSettingsKeyTable.Families.Count);

        foreach (var family in EncoderSettingsKeyTable.Families)
            Assert.True(EncoderSettingsKeyTable.For(family).Count >= 12,
                $"{family} contributes only {EncoderSettingsKeyTable.For(family).Count} keys.");

        foreach (var family in EncoderSettingsKeyTable.Families)
        {
            var keys = EncoderSettingsKeyTable.For(family).Select(key => key.Key).ToArray();
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void TheBFrameCount_IsSpelledBframesOnQsvAndBfInEveryOtherFamily()
    {
        Assert.Contains(EncoderSettingsKeyTable.For(EncoderSettingsKeyTable.Qsv), key => key.Key == "bframes");

        foreach (var family in EncoderSettingsKeyTable.Families.Where(name => name != EncoderSettingsKeyTable.Qsv))
        {
            Assert.Contains(EncoderSettingsKeyTable.For(family), key => key.Key == "bf");
            Assert.DoesNotContain(EncoderSettingsKeyTable.For(family), key => key.Key == "bframes");
        }
    }

    [Fact]
    public void TheNvencPresetKey_IsPresetOnTheTextureEncodersAndPreset2OnTheLegacyOnes()
    {
        var texture = EncoderSettingsKeyTable.For(EncoderSettingsKeyTable.NvencTexture).Select(key => key.Key).ToArray();
        var legacy = EncoderSettingsKeyTable.For(EncoderSettingsKeyTable.NvencLegacy).Select(key => key.Key).ToArray();

        Assert.Contains("preset", texture);
        Assert.DoesNotContain("preset2", texture);
        Assert.Contains("preset2", legacy);
        Assert.DoesNotContain("preset", legacy);

        Assert.Contains("adaptive_quantization", texture);
        Assert.Contains("device", texture);
        Assert.Contains("psycho_aq", legacy);
        Assert.Contains("gpu", legacy);
    }

    [Fact]
    public void Amf_DeclaresNoMaxBitrateAndNoTune()
    {
        var amf = EncoderSettingsKeyTable.For(EncoderSettingsKeyTable.Amf).Select(key => key.Key).ToArray();

        Assert.DoesNotContain("max_bitrate", amf);
        Assert.DoesNotContain("tune", amf);

        Assert.Contains("cqp", amf);
    }

    [Fact]
    public void KeyintSec_IsAnIntegerInEveryFamilyBecauseItIsSecondsEverywhere()
    {
        foreach (var family in EncoderSettingsKeyTable.Families)
        {
            var keyintSec = Assert.Single(EncoderSettingsKeyTable.For(family), key => key.Key == "keyint_sec");
            Assert.Equal(ObsSettingsValueType.Number, keyintSec.Type);
        }
    }

    private static string Describe(object value) => value switch
    {
        string text => $"'{text}'",
        _ => value.ToString() ?? "(null)"
    };
}
