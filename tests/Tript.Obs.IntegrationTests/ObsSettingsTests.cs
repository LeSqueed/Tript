// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.InteropServices;
using System.Text;
using Tript.Obs.Interop;
using Xunit;

namespace Tript.Obs.IntegrationTests;

// The settings object against the real runtime: what it stores, what it converts, what it refuses
// to convert, and how long it lives. Most of these tests start no OBS context, and that is a
// finding rather than a shortcut — obs_data turns out to be independent of it in both directions.
public sealed class ObsSettingsTests
{
    // ---- lifetime ----

    // The measurement that decided which safe-handle base this type uses. obs_data allocates on
    // bmem and the OBS core keeps no registry of these objects, so nothing about them waits for
    // obs_startup.
    [Fact]
    public void ASettingsObject_IsUsableWithNoObsContextRunning()
    {
        Assert.False(ObsRuntime.IsInitialized);

        using var settings = new ObsSettings();
        settings.SetInt("bitrate", 12000);

        Assert.Equal(12000, settings.GetInt("bitrate"));
    }

    // The other half of the same question, and the one that would have made a context-scoped handle
    // wrong: a settings object created inside a context is still readable after obs_shutdown, and
    // still has to be released by us. A handle that declined to release after shutdown would leak
    // every one of them.
    [Fact]
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
        // One warm-up cycle first: the very first call through a generated stub allocates the
        // marshalling machinery, which would otherwise be counted as a leak.
        Cycle();

        var before = ObsRuntime.LiveAllocationCount;

        for (var i = 0; i < 2000; i++)
            Cycle();

        Assert.Equal(before, ObsRuntime.LiveAllocationCount);

        static void Cycle()
        {
            using var settings = new ObsSettings();
            settings.SetString("rate_control", "CBR");
            settings.SetDefaultString("rate_control", "VBR");
            settings.SetInt("bitrate", 6000);

            using var child = new ObsSettings();
            child.SetBool("nested", true);
            settings.SetObject("child", child);

            using var array = new ObsSettingsArray();
            array.Add(child);
            settings.SetArray("list", array);

            _ = settings.ToJson();
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

    // ---- string ownership ----

    // The question the previous layer was caught out by: obs_reset_video keeps the caller's
    // graphics-module pointer without copying it, so that string has to outlive the call. obs_data
    // does the opposite — it copies both the key and the value — which is why nothing here is
    // interned and why passing a marshalled temporary is safe. Proved by handing libobs a buffer we
    // own, then destroying it before reading back.
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

        // Scribbled before the free so a reader that kept the pointer sees changed bytes rather
        // than depending on the allocator to reuse the block.
        new Span<byte>((void*)nativeName, Encoding.UTF8.GetByteCount(name)).Fill(0x58);
        new Span<byte>((void*)nativeValue, Encoding.UTF8.GetByteCount(value)).Fill(0x59);
        Utf8Marshal.Free(nativeName);
        Utf8Marshal.Free(nativeValue);

        Assert.Equal(value, settings.GetString(name));
    }

    [Fact]
    public void AStringSetting_RoundTripsNonAsciiByteIdentically()
    {
        const string key = "café — 日本語 — Ω — 🎮";
        const string value = "ünïcödé — ✓ — 𝄞";

        using var settings = new ObsSettings();
        settings.SetString(key, value);

        Assert.Equal(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(settings.GetString(key)));
        Assert.True(settings.HasUserValue(key));
    }

    // ---- typed round trips ----

    [Fact]
    public void EveryValueType_RoundTripsThroughOneSettingsObject()
    {
        using var settings = new ObsSettings();
        using var child = new ObsSettings();
        using var array = new ObsSettingsArray();

        child.SetInt("depth", 1);
        array.Add(child);

        settings.SetString("text", "value");
        settings.SetInt("integer", -7);
        settings.SetDouble("real", 1.25);
        settings.SetBool("flag", true);
        settings.SetObject("object", child);
        settings.SetArray("array", array);

        Assert.Equal("value", settings.GetString("text"));
        Assert.Equal(-7, settings.GetInt("integer"));
        Assert.Equal(1.25, settings.GetDouble("real"));
        Assert.True(settings.GetBool("flag"));

        using var readObject = settings.GetObject("object");
        Assert.NotNull(readObject);
        Assert.Equal(1, readObject.GetInt("depth"));

        using var readArray = settings.GetArray("array");
        Assert.NotNull(readArray);
        Assert.Equal(1, readArray.Count);
    }

    // libobs stores every number as long long. A binding that narrowed it to int would work for
    // every value anyone tried by hand.
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

    // ---- type mismatch ----
    //
    // Integers and doubles share one storage slot and convert freely. Nothing else converts at all,
    // and none of the failures is reported.

    [Fact]
    public void ANumber_ReadAsAString_IsEmptyRatherThanItsDigits()
    {
        using var settings = new ObsSettings();
        settings.SetInt("bitrate", 4242);

        Assert.Equal(string.Empty, settings.GetString("bitrate"));
        Assert.True(settings.HasUserValue("bitrate"));
    }

    [Fact]
    public void AString_ReadAsANumber_IsZeroRatherThanParsed()
    {
        using var settings = new ObsSettings();
        settings.SetString("bitrate", "6000");

        Assert.Equal(0, settings.GetInt("bitrate"));
        Assert.Equal(0.0, settings.GetDouble("bitrate"));
    }

    // The sharpest of the three: no value of any other type is ever truthy, so a flag written as
    // the integer 1 — the shape a configuration file round-tripped through another tool tends to
    // take — reads as false.
    [Fact]
    public void ABoolean_ConvertsToAndFromNoOtherType()
    {
        using var settings = new ObsSettings();

        settings.SetInt("fromInteger", 1);
        settings.SetString("fromString", "true");
        settings.SetDouble("fromDouble", 1.0);
        Assert.False(settings.GetBool("fromInteger"));
        Assert.False(settings.GetBool("fromString"));
        Assert.False(settings.GetBool("fromDouble"));

        settings.SetBool("flag", true);
        Assert.Equal(0, settings.GetInt("flag"));
        Assert.Equal(0.0, settings.GetDouble("flag"));
        Assert.Equal(string.Empty, settings.GetString("flag"));
    }

    [Fact]
    public void ADouble_ReadAsAnInteger_TruncatesTowardZero()
    {
        using var settings = new ObsSettings();
        settings.SetDouble("positive", 3.75);
        settings.SetDouble("negative", -3.75);

        Assert.Equal(3, settings.GetInt("positive"));
        Assert.Equal(-3, settings.GetInt("negative"));

        settings.SetInt("integer", 9);
        Assert.Equal(9.0, settings.GetDouble("integer"));
    }

    // Nothing distinguishes an absent key from one set to the zero value of its type, which is why
    // HasUserValue exists and why every round-trip test above asserts on it.
    [Fact]
    public void AnAbsentKey_ReadsAsEmptyZeroAndFalse()
    {
        using var settings = new ObsSettings();

        Assert.Equal(string.Empty, settings.GetString("absent"));
        Assert.Equal(0, settings.GetInt("absent"));
        Assert.Equal(0.0, settings.GetDouble("absent"));
        Assert.False(settings.GetBool("absent"));
        Assert.Null(settings.GetObject("absent"));
        Assert.Null(settings.GetArray("absent"));

        Assert.False(settings.HasUserValue("absent"));
        Assert.False(settings.HasDefaultValue("absent"));
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

    // A null string is stored as an empty one and still counts as configured — so it is not a way
    // to unset a key. UnsetUserValue is.
    [Fact]
    public void ANullStringValue_IsStoredAsAnEmptyStringAndStillCountsAsConfigured()
    {
        using var settings = new ObsSettings();
        settings.SetString("profile", null);

        Assert.Equal(string.Empty, settings.GetString("profile"));
        Assert.True(settings.HasUserValue("profile"));
    }

    // ---- defaults ----

    [Fact]
    public void ADefault_IsReadUntilAUserValueShadowsIt()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultString("rate_control", "CBR");

        Assert.Equal("CBR", settings.GetString("rate_control"));
        Assert.True(settings.HasDefaultValue("rate_control"));
        Assert.False(settings.HasUserValue("rate_control"));

        settings.SetString("rate_control", "CQP");

        Assert.Equal("CQP", settings.GetString("rate_control"));
        Assert.Equal("CBR", settings.GetDefaultString("rate_control"));
        Assert.True(settings.HasUserValue("rate_control"));
    }

    [Fact]
    public void UnsettingAUserValue_RestoresTheDefaultWhileErasingRemovesBoth()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultInt("bitrate", 6000);
        settings.SetInt("bitrate", 45000);

        settings.UnsetUserValue("bitrate");
        Assert.Equal(6000, settings.GetInt("bitrate"));
        Assert.True(settings.HasDefaultValue("bitrate"));
        Assert.False(settings.HasUserValue("bitrate"));

        settings.Erase("bitrate");
        Assert.Equal(0, settings.GetInt("bitrate"));
        Assert.False(settings.HasDefaultValue("bitrate"));
    }

    // The trap worth knowing about before writing any encoder configuration. An entry holds one
    // type, so writing a user value of a different type over a default does not shadow it — it
    // destroys it.
    [Fact]
    public void WritingAUserValueOfADifferentType_DestroysTheDefaultRatherThanShadowingIt()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultString("preset", "veryfast");

        settings.SetInt("preset", 5);

        Assert.True(settings.HasDefaultValue("preset"));
        Assert.Equal(string.Empty, settings.GetDefaultString("preset"));
        Assert.Equal(0, settings.GetDefaultInt("preset"));

        settings.UnsetUserValue("preset");
        Assert.Equal(string.Empty, settings.GetString("preset"));
    }

    // Which is not what happens when the types agree: then the default survives untouched.
    [Fact]
    public void WritingAUserValueOfTheSameType_LeavesTheDefaultIntact()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultString("preset", "veryfast");
        settings.SetString("preset", "placebo");

        settings.UnsetUserValue("preset");
        Assert.Equal("veryfast", settings.GetString("preset"));
    }

    [Fact]
    public void Clear_RemovesUserValuesAndLeavesDefaults()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultInt("bitrate", 6000);
        settings.SetInt("bitrate", 45000);
        settings.SetString("preset", "placebo");

        settings.Clear();

        Assert.False(settings.HasUserValue("bitrate"));
        Assert.False(settings.HasUserValue("preset"));
        Assert.True(settings.HasDefaultValue("bitrate"));
        Assert.Equal(6000, settings.GetInt("bitrate"));
    }

    // The defaults come back as a separate object in which they are user values, which is what
    // makes them serialisable and diffable — the shape recommended for dumping an
    // encoder's declared key set.
    [Fact]
    public void GetDefaults_ReturnsTheDefaultsAsAnObjectOfUserValues()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultString("rate_control", "CBR");
        settings.SetDefaultInt("bitrate", 6000);
        settings.SetString("rate_control", "CQP");

        using var defaults = settings.GetDefaults();

        Assert.Equal("CBR", defaults.GetString("rate_control"));
        Assert.Equal(6000, defaults.GetInt("bitrate"));
        Assert.True(defaults.HasUserValue("rate_control"));
    }

    // ---- nesting ----

    [Fact]
    public void ANestedObject_IsStoredByReferenceRatherThanCopied()
    {
        using var parent = new ObsSettings();
        using var child = new ObsSettings();

        child.SetString("inner", "before");
        parent.SetObject("child", child);
        child.SetString("inner", "after");

        using var fetched = parent.GetObject("child");
        Assert.NotNull(fetched);
        Assert.Equal("after", fetched.GetString("inner"));
    }

    // The parent holds its own reference, so disposing ours does not take the child with it. This
    // is what lets a caller build a settings tree and dispose the pieces as it goes.
    [Fact]
    public void ANestedObject_SurvivesTheCallerDisposingItsOwnReference()
    {
        using var parent = new ObsSettings();

        using (var child = new ObsSettings())
        {
            child.SetString("inner", "value");
            parent.SetObject("child", child);
        }

        using var fetched = parent.GetObject("child");
        Assert.NotNull(fetched);
        Assert.Equal("value", fetched.GetString("inner"));
    }

    [Fact]
    public void ANullNestedObject_StoresAJsonNullWhileANullArrayStoresAnEmptyArray()
    {
        using var settings = new ObsSettings();
        settings.SetObject("object", null);
        settings.SetArray("array", null);

        Assert.Null(settings.GetObject("object"));
        Assert.Contains("\"object\":null", settings.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"array\":[]", settings.ToJson(), StringComparison.Ordinal);
    }

    // ---- arrays ----

    [Fact]
    public void AnArray_KeepsItsElementsInOrder()
    {
        using var array = new ObsSettingsArray();

        for (var i = 0; i < 4; i++)
        {
            using var element = new ObsSettings();
            element.SetInt("index", i);
            Assert.Equal(i, array.Add(element));
        }

        Assert.Equal(4, array.Count);

        for (var i = 0; i < 4; i++)
        {
            using var element = array[i];
            Assert.Equal(i, element.GetInt("index"));
        }
    }

    [Fact]
    public void AnArray_SupportsInsertionAndRemovalAtAnIndex()
    {
        using var array = new ObsSettingsArray();
        using var first = new ObsSettings();
        using var second = new ObsSettings();

        first.SetString("id", "first");
        second.SetString("id", "second");

        array.Add(first);
        array.Insert(0, second);

        using (var head = array[0])
            Assert.Equal("second", head.GetString("id"));

        array.RemoveAt(0);
        Assert.Equal(1, array.Count);

        using var remaining = array[0];
        Assert.Equal("first", remaining.GetString("id"));
    }

    // libobs answers an out-of-range index with null rather than a fault, which would surface as a
    // null reference somewhere else entirely.
    [Fact]
    public void AnArrayIndexOutOfRange_IsRefusedWhereItWasAsked()
    {
        using var array = new ObsSettingsArray();
        using var element = new ObsSettings();
        array.Add(element);

        Assert.Throws<ArgumentOutOfRangeException>(() => array[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array.RemoveAt(1));
    }

    // Arrays are held by reference like nested objects are, so an edit after the fact is visible
    // through the settings object that stored it.
    [Fact]
    public void AnArrayStoredInASettingsObject_ReflectsLaterEdits()
    {
        using var settings = new ObsSettings();
        using var array = new ObsSettingsArray();
        using var element = new ObsSettings();

        element.SetInt("index", 0);
        array.Add(element);
        settings.SetArray("list", array);

        using var second = new ObsSettings();
        second.SetInt("index", 1);
        array.Add(second);

        using var fetched = settings.GetArray("list");
        Assert.NotNull(fetched);
        Assert.Equal(2, fetched.Count);
    }

    // ---- merging ----

    // A shallow merge. A child object present on both sides is replaced whole rather than merged
    // into, so keys the target had inside it are gone — which is the opposite of what "apply" reads
    // like and is stated in no header.
    [Fact]
    public void Apply_OverwritesMatchingKeysAndReplacesNestedObjectsWholesale()
    {
        using var target = new ObsSettings();
        using var source = new ObsSettings();

        target.SetInt("kept", 1);
        target.SetInt("overwritten", 1);

        using (var targetChild = new ObsSettings())
        {
            targetChild.SetInt("lost", 1);
            target.SetObject("child", targetChild);
        }

        source.SetInt("overwritten", 2);
        source.SetInt("added", 3);

        using (var sourceChild = new ObsSettings())
        {
            sourceChild.SetInt("added", 2);
            source.SetObject("child", sourceChild);
        }

        target.Apply(source);

        Assert.Equal(1, target.GetInt("kept"));
        Assert.Equal(2, target.GetInt("overwritten"));
        Assert.Equal(3, target.GetInt("added"));

        using var merged = target.GetObject("child");
        Assert.NotNull(merged);
        Assert.Equal(2, merged.GetInt("added"));
        Assert.False(merged.HasUserValue("lost"));
    }

    // ---- serialisation ----

    [Fact]
    public void AJsonDocument_RoundTripsThroughASettingsObject()
    {
        const string json = """{"bitrate":45000,"rate_control":"CQVBR","lookahead":true,"ratio":1.5,"child":{"depth":2},"list":[{"index":0}]}""";

        using var settings = ObsSettings.FromJson(json)!;
        Assert.NotNull(settings);

        Assert.Equal(45000, settings.GetInt("bitrate"));
        Assert.Equal("CQVBR", settings.GetString("rate_control"));
        Assert.True(settings.GetBool("lookahead"));
        Assert.Equal(1.5, settings.GetDouble("ratio"));

        using var child = settings.GetObject("child");
        Assert.Equal(2, child!.GetInt("depth"));

        using var list = settings.GetArray("list");
        Assert.Equal(1, list!.Count);

        // Everything parsed arrives as a user value, so a loaded configuration is distinguishable
        // from a set of defaults.
        Assert.True(settings.HasUserValue("bitrate"));
        Assert.False(settings.HasDefaultValue("bitrate"));
    }

    [Fact]
    public void MalformedJson_YieldsNoSettingsObject()
    {
        Assert.Null(ObsSettings.FromJson("{not json"));
        Assert.Null(ObsSettings.FromJsonFile("/nonexistent/tript-settings.json"));
    }

    // libobs caches the serialised text on the object and the next serialisation of the same object
    // frees the previous buffer. Two results held at once would leave the first reading freed
    // memory, so the binding copies before returning — this is what proves it does.
    [Fact]
    public void SuccessiveJsonReads_EachReturnTheirOwnResult()
    {
        using var settings = new ObsSettings();
        settings.SetInt("bitrate", 45000);
        settings.SetDefaultString("preset", "veryfast");

        // All three held at once, and all three different — which is what a binding handing back
        // libobs's cached buffer could not manage, because the second call frees the first.
        var plain = settings.ToJson();
        var withDefaults = settings.ToJson(includeDefaults: true);
        var pretty = settings.ToJson(pretty: true);

        Assert.Equal("""{"bitrate":45000}""", plain);
        Assert.Contains("veryfast", withDefaults, StringComparison.Ordinal);
        Assert.Contains('\n', pretty);
        Assert.DoesNotContain('\n', plain);
    }

    // A key that only has a default does not appear in the plain serialisation and does in the one
    // that includes defaults. That difference is what makes "was this configured?" answerable from a
    // saved file as well as from a live object.
    [Fact]
    public void JsonWithDefaults_IncludesKeysThatOnlyHaveADefault()
    {
        using var settings = new ObsSettings();
        settings.SetDefaultString("preset", "veryfast");
        settings.SetInt("bitrate", 45000);

        Assert.DoesNotContain("preset", settings.ToJson(), StringComparison.Ordinal);
        Assert.Contains("veryfast", settings.ToJson(includeDefaults: true), StringComparison.Ordinal);
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

    // ---- iteration ----

    // The route to an encoder's declared key set: obs_encoder_defaults returns an object carrying
    // every key the plugin has a default for, and this is how it is read off. Entries that exist
    // only as a default are included, which is the whole point.
    [Fact]
    public void Iteration_ReportsEveryEntryWithItsTypeAndWhereItsValueCameFrom()
    {
        using var settings = new ObsSettings();
        using var child = new ObsSettings();
        using var array = new ObsSettingsArray();

        settings.SetString("text", "value");
        settings.SetInt("integer", 1);
        settings.SetDouble("real", 1.5);
        settings.SetBool("flag", true);
        settings.SetObject("object", child);
        settings.SetArray("array", array);
        settings.SetDefaultInt("defaultOnly", 7);

        var entries = settings.EnumerateEntries().ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        Assert.Equal(7, entries.Count);

        Assert.Equal(ObsSettingsValueType.String, entries["text"].ValueType);
        Assert.Equal(ObsSettingsValueType.Number, entries["integer"].ValueType);
        Assert.Equal(ObsSettingsNumberType.Integer, entries["integer"].NumberType);
        Assert.Equal(ObsSettingsNumberType.Double, entries["real"].NumberType);
        Assert.Equal(ObsSettingsValueType.Boolean, entries["flag"].ValueType);
        Assert.Equal(ObsSettingsValueType.Object, entries["object"].ValueType);
        Assert.Equal(ObsSettingsValueType.Array, entries["array"].ValueType);

        Assert.True(entries["text"].HasUserValue);
        Assert.False(entries["text"].HasDefaultValue);
        Assert.False(entries["defaultOnly"].HasUserValue);
        Assert.True(entries["defaultOnly"].HasDefaultValue);
    }

    [Fact]
    public void Iteration_OfAnEmptySettingsObject_YieldsNothingAndLeaksNothing()
    {
        using var settings = new ObsSettings();

        var before = ObsRuntime.LiveAllocationCount;
        for (var i = 0; i < 100; i++)
            Assert.Empty(settings.EnumerateEntries());

        Assert.Equal(before, ObsRuntime.LiveAllocationCount);
    }
}
