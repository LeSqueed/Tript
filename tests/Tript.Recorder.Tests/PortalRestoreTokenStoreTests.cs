// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class PortalRestoreTokenStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-portal-token", Guid.NewGuid().ToString("N"));

    private PortalRestoreTokenStore NewStore() =>
        new(Path.Combine(_root, "config", PortalRestoreTokenStore.FileName));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ASavedToken_IsLoadedBack()
    {
        var store = NewStore();

        Assert.True(store.Save("f3b1c2d4-token"));

        Assert.Equal("f3b1c2d4-token", NewStore().Load());
    }

    [Fact]
    public void NoFile_LoadsAsNoToken() =>
        Assert.Null(NewStore().Load());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankToken_IsNotSavedOverAnExistingOne(string? blank)
    {
        var store = NewStore();
        store.Save("kept");

        Assert.False(store.Save(blank));
        Assert.Equal("kept", store.Load());
    }

    [Fact]
    public void SavingTheSameTokenAgain_ReportsNoChange()
    {
        var store = NewStore();
        store.Save("same");

        Assert.False(store.Save("same"));
    }

    [Fact]
    public void SurroundingWhitespace_IsNotPartOfTheToken()
    {
        var store = NewStore();
        store.Save("  padded\n");

        Assert.Equal("padded", store.Load());
    }

    [Fact]
    public void ForgettingTheToken_MakesTheNextLoadAskAgain()
    {
        var store = NewStore();
        store.Save("remembered");

        Assert.True(store.Forget());

        Assert.Null(store.Load());
    }

    [Fact]
    public void ForgettingWithNothingRemembered_ReportsNoChange() =>
        Assert.False(NewStore().Forget());
}
