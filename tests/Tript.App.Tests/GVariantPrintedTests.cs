// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Shell.Linux;
using Xunit;

namespace Tript.App.Tests;

public sealed class GVariantPrintedTests
{
    [Fact]
    public void AnAnnotatedObjectPath_IsReadAsAnObjectPath()
    {
        var reply = GVariantPrinted.Parse("(objectpath '/org/freedesktop/portal/desktop/request/1_42/tript1',)");

        var path = Assert.Single(reply.Items);
        Assert.Equal(GVariantKind.ObjectPath, path.Kind);
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/tript1", path.AsString());
    }

    [Theory]
    [InlineData("\"it's\"", "it's")]
    [InlineData(@"'it\'s'", "it's")]
    [InlineData(@"'back\\slash'", @"back\slash")]
    [InlineData(@"'été'", "été")]
    [InlineData(@"'\U0001f3ae'", "🎮")]
    [InlineData(@"'a\nb\tc'", "a\nb\tc")]
    [InlineData("''", "")]
    public void AQuotedString_IsUnescaped(string printed, string expected) =>
        Assert.Equal(expected, GVariantPrinted.Parse(printed).AsString());

    [Fact]
    public void AVariantDictionary_LooksUpItsValuesUnboxed()
    {
        var dictionary = GVariantPrinted.Parse(
            "{'session_handle': <objectpath '/s/1'>, 'count': <uint32 7>, 'label': <'x'>, 'on': <true>}");

        Assert.Equal("/s/1", dictionary.Lookup("session_handle")!.AsString());
        Assert.Equal(7u, dictionary.Lookup("count")!.AsUInt32());
        Assert.Equal("x", dictionary.Lookup("label")!.AsString());
        Assert.True(dictionary.Lookup("on")!.AsBoolean());
        Assert.Null(dictionary.Lookup("missing"));
    }

    [Fact]
    public void APortalResponse_ReadsItsCodeAndResults()
    {
        var response = GVariantPrinted.Parse(
            "(uint32 0, {'shortcuts': <[('toggle-recording', {'description': <'Start or stop recording'>, " +
            "'trigger_description': <'Ctrl+Shift+F9'>})]>})");

        Assert.Equal(0u, response[0]!.AsUInt32());
        var shortcut = Assert.Single(response[1]!.Lookup("shortcuts")!.Items);
        Assert.Equal("toggle-recording", shortcut[0]!.AsString());
        Assert.Equal("Ctrl+Shift+F9", shortcut[1]!.Lookup("trigger_description")!.AsString());
    }

    [Fact]
    public void AnActivatedSignal_ReadsItsSessionShortcutAndTimestamp()
    {
        var activated = GVariantPrinted.Parse(
            "(objectpath '/org/freedesktop/portal/desktop/session/1_42/tript2', 'quick-clip', uint64 1234567, @a{sv} {})");

        Assert.Equal("/org/freedesktop/portal/desktop/session/1_42/tript2", activated[0]!.AsString());
        Assert.Equal("quick-clip", activated[1]!.AsString());
        Assert.Equal(1234567ul, activated[2]!.AsUInt64());
        Assert.Empty(activated[3]!.Entries);
    }

    [Fact]
    public void AVersionProperty_ReadsAsAnUnsignedNumber()
    {
        var property = GVariantPrinted.Parse("(<uint32 2>,)");

        Assert.Equal(2u, property[0]!.AsUInt32());
    }

    [Fact]
    public void NumbersOutsideUnsigned32Bits_AreNotReadAsUInt32()
    {
        Assert.Null(GVariantPrinted.Parse("uint64 4294967296").AsUInt32());
        Assert.Null(GVariantPrinted.Parse("int32 -1").AsUInt32());
        Assert.Equal(255u, GVariantPrinted.Parse("byte 0xff").AsUInt32());
    }

    [Fact]
    public void AStringArrayAndEmptyContainers_AreRead()
    {
        var capabilities = GVariantPrinted.Parse("(['body', 'body-markup', 'actions'],)");
        Assert.Equal(["body", "body-markup", "actions"], capabilities[0]!.Items.Select(item => item.AsString()));

        Assert.Empty(GVariantPrinted.Parse("()").Items);
        Assert.Empty(GVariantPrinted.Parse("@as []").Items);
        Assert.Empty(GVariantPrinted.Parse("@a{sv} {}").Entries);
    }

    [Fact]
    public void AStandaloneDictionaryEntry_IsReadAsOneEntry()
    {
        var entry = GVariantPrinted.Parse("{'key', <'value'>}");

        Assert.Equal("value", entry.Lookup("key")!.AsString());
    }

    [Theory]
    [InlineData("('unterminated")]
    [InlineData("(uint32 1")]
    [InlineData("{'a' 'b'}")]
    [InlineData("'x' trailing")]
    [InlineData(@"'\u12'")]
    [InlineData("")]
    public void MalformedText_IsRejected(string printed) =>
        Assert.Throws<FormatException>(() => GVariantPrinted.Parse(printed));
}
