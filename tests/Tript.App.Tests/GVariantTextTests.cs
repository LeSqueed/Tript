// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class GVariantTextTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", @"'it\'s'")]
    [InlineData(@"C:\path", @"'C:\\path'")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("line\nbreak", @"'line\u000abreak'")]
    [InlineData("tab\there", @"'tab\u0009here'")]
    [InlineData("nul\0dropped", "'nuldropped'")]
    [InlineData("café 🎮", "'café 🎮'")]
    public void AString_IsQuotedAndEscapedForTheTextFormat(string value, string expected) =>
        Assert.Equal(expected, GVariantText.String(value));

    [Fact]
    public void ASingleItemTuple_KeepsItsTrailingComma() =>
        Assert.Equal("('only',)", GVariantText.Tuple(GVariantText.String("only")));

    [Fact]
    public void ATupleOfSeveralItems_IsCommaSeparated() =>
        Assert.Equal("('a', uint32 7, true)",
            GVariantText.Tuple(GVariantText.String("a"), GVariantText.UInt32(7), GVariantText.Boolean(true)));

    [Fact]
    public void AnEmptyDictionaryOrArray_CarriesItsTypeSoItCanBeParsed()
    {
        Assert.Equal("@a{sv} {}", GVariantText.VariantDictionary([]));
        Assert.Equal("@as []", GVariantText.StringArray([]));
    }

    [Fact]
    public void ADictionary_BoxesEveryValueInAVariant() =>
        Assert.Equal("@a{sv} {'handle_token': <'t1'>, 'count': <uint32 2>}",
            GVariantText.VariantDictionary(
            [
                new("handle_token", GVariantText.String("t1")),
                new("count", GVariantText.UInt32(2)),
            ]));

    [Fact]
    public void AnObjectPathAndSignedNumbers_AreTypedExplicitly()
    {
        Assert.Equal("objectpath '/org/example/1'", GVariantText.ObjectPath("/org/example/1"));
        Assert.Equal("int32 -1", GVariantText.Int32(-1));
    }

    [Fact]
    public void BuiltText_ReadsBackThroughThePrintedParser()
    {
        var text = GVariantText.Tuple(
            GVariantText.ObjectPath("/session/1"),
            GVariantText.Array("(sa{sv})",
            [
                GVariantText.Tuple(GVariantText.String("toggle-recording"),
                    GVariantText.VariantDictionary([new("preferred_trigger", GVariantText.String("CTRL+F9"))])),
            ]),
            GVariantText.String("it's \\ fine"));

        var parsed = GVariantPrinted.Parse(text);

        Assert.Equal(GVariantKind.ObjectPath, parsed[0]!.Kind);
        Assert.Equal("/session/1", parsed[0]!.AsString());
        Assert.Equal("CTRL+F9", parsed[1]![0]![1]!.Lookup("preferred_trigger")!.AsString());
        Assert.Equal("it's \\ fine", parsed[2]!.AsString());
    }

    [SkippableFact]
    public void BuiltText_IsAcceptedByGLibAndItsPrintedFormReadsBack()
    {
        Skip.IfNot(GLibRoundTrip.Available, "GLib is not installed on this machine.");

        var text = GVariantText.Tuple(
            GVariantText.String("Tript"),
            GVariantText.UInt32(0),
            GVariantText.String("it's a \"quote\"\n\\ and ünïcode"),
            GVariantText.StringArray([]),
            GVariantText.VariantDictionary(
            [
                new("session_handle", GVariantText.ObjectPath("/org/freedesktop/portal/desktop/session/1_42/t")),
                new("suppress-sound", GVariantText.Boolean(true)),
            ]),
            GVariantText.Int32(-1));

        var parsed = GVariantPrinted.Parse(GLibRoundTrip.Print(text));

        Assert.Equal("Tript", parsed[0]!.AsString());
        Assert.Equal(0u, parsed[1]!.AsUInt32());
        Assert.Equal("it's a \"quote\"\n\\ and ünïcode", parsed[2]!.AsString());
        Assert.Empty(parsed[3]!.Items);
        Assert.Equal(GVariantKind.ObjectPath, parsed[4]!.Lookup("session_handle")!.Kind);
        Assert.Equal("/org/freedesktop/portal/desktop/session/1_42/t",
            parsed[4]!.Lookup("session_handle")!.AsString());
        Assert.True(parsed[4]!.Lookup("suppress-sound")!.AsBoolean());
        Assert.Equal("-1", parsed[5]!.Scalar);
    }
}

internal static unsafe class GLibRoundTrip
{
    internal static bool Available
    {
        get
        {
            try
            {
                Print("('probe',)");
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    internal static string Print(string text)
    {
        GioNative.GError* error = null;
        var value = GioNative.g_variant_parse(IntPtr.Zero, text, IntPtr.Zero, IntPtr.Zero, &error);
        if (value == IntPtr.Zero)
            throw GioNative.TakeError(error, "Parsing");

        try
        {
            return GioNative.TakeUtf8(GioNative.g_variant_print(value, 1));
        }
        finally
        {
            GioNative.g_variant_unref(value);
        }
    }
}
