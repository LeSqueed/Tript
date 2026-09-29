// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using System.Text;
using Tript.Obs.Interop;
using Xunit;

namespace Tript.Obs.IntegrationTests;

public sealed class ObsSettingsTests
{
    [Fact]
    public void ASettingsObject_IsUsableWithNoObsContextRunning()
    {
        Assert.False(ObsRuntime.IsInitialized);

        using var settings = new ObsSettings();
        settings.SetInt("bitrate", 12000);

        Assert.Equal(12000, settings.GetInt("bitrate"));
    }

    [SkippableFact]
    public void ASettingsObject_OutlivesTheContextItWasCreatedIn()
    {
        ObsSettings survivor;

        using (var session = ObsSession.Start())
        {
            survivor = new ObsSettings();
            survivor.SetString("made", "inside the context");
            Assert.NotNull(session.Runtime);
        }

        Assert.False(ObsRuntime.IsInitialized);
        Assert.Equal("inside the context", survivor.GetString("made"));

        survivor.Dispose();
    }

    [Fact]
    public void RepeatedCreateAndRelease_LeavesNoLibobsAllocationsBehind()
    {
        Cycle();

        var before = ObsRuntime.LiveAllocationCount;

        for (var i = 0; i < 2000; i++)
            Cycle();

        Assert.Equal(before, ObsRuntime.LiveAllocationCount);

        static void Cycle()
        {
            using var settings = new ObsSettings();
            settings.SetString("rate_control", "CBR");
            settings.SetInt("bitrate", 6000);
            settings.SetBool("lookahead", true);
            _ = settings.GetString("rate_control");
        }
    }

    [Fact]
    public void Dispose_IsIdempotentAndUseAfterwardsIsRefused()
    {
        var settings = new ObsSettings();
        settings.Dispose();
        settings.Dispose();

        Assert.Throws<ObjectDisposedException>(() => settings.SetInt("bitrate", 1));
        Assert.Throws<ObjectDisposedException>(() => settings.GetInt("bitrate"));
    }

    [Fact]
    public unsafe void ASettingsObject_CopiesBothTheKeyAndTheValueItIsGiven()
    {
        var setString = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)
            NativeLibrary.GetExport(ObsLibrary.EnsureLoaded(), "obs_data_set_string");

        const string name = "rate_control";
        const string value = "CQVBR";

        using var settings = new ObsSettings();

        var nativeName = Utf8Marshal.Allocate(name);
        var nativeValue = Utf8Marshal.Allocate(value);

        setString(settings.Pointer, nativeName, nativeValue);

        new Span<byte>((void*)nativeName, Encoding.UTF8.GetByteCount(name)).Fill(0x58);
        new Span<byte>((void*)nativeValue, Encoding.UTF8.GetByteCount(value)).Fill(0x59);
        Utf8Marshal.Free(nativeName);
        Utf8Marshal.Free(nativeValue);

        Assert.Equal(value, settings.GetString(name));
    }

    [Fact]
    public void AStringSetting_RoundTripsNonAsciiByteIdentically()
    {
        const string key = "café \u2014 日本語 \u2014 Ω \u2014 🎮";
        const string value = "ünïcödé \u2014 ✓ \u2014 𝄞";

        using var settings = new ObsSettings();
        settings.SetString(key, value);

        Assert.Equal(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(settings.GetString(key)));
        Assert.True(settings.HasUserValue(key));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(int.MaxValue + 1L)]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void AnIntegerSetting_KeepsTheFullSixtyFourBitRange(long value)
    {
        using var settings = new ObsSettings();
        settings.SetInt("n", value);

        Assert.Equal(value, settings.GetInt("n"));
    }

    [Fact]
    public void ANumber_ReadAsAString_IsEmptyRatherThanItsDigits()
    {
        using var settings = new ObsSettings();
        settings.SetInt("bitrate", 4242);

        Assert.Equal(string.Empty, settings.GetString("bitrate"));
        Assert.True(settings.HasUserValue("bitrate"));
    }

    [Fact]
    public void Keys_AreMatchedCaseSensitively()
    {
        using var settings = new ObsSettings();
        settings.SetInt("Bitrate", 6000);

        Assert.Equal(0, settings.GetInt("bitrate"));
        Assert.False(settings.HasUserValue("bitrate"));
        Assert.Equal(6000, settings.GetInt("Bitrate"));
    }

    [Fact]
    public void ANullStringValue_IsStoredAsAnEmptyStringAndStillCountsAsConfigured()
    {
        using var settings = new ObsSettings();
        settings.SetString("profile", null);

        Assert.Equal(string.Empty, settings.GetString("profile"));
        Assert.True(settings.HasUserValue("profile"));
    }

    [Fact]
    public void SavedJson_ReadsBackAsTheSameSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tript-settings-{Guid.NewGuid():N}.json");

        try
        {
            using (var settings = new ObsSettings())
            {
                settings.SetString("rate_control", "CQVBR");
                settings.SetInt("bitrate", 45000);
                Assert.True(settings.SaveJson(path));
            }

            using var loaded = ObsSettings.FromJsonFile(path);
            Assert.NotNull(loaded);
            Assert.Equal("CQVBR", loaded.GetString("rate_control"));
            Assert.Equal(45000, loaded.GetInt("bitrate"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
