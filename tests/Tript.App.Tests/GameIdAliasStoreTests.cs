// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Models;
using Xunit;

namespace Tript.App.Tests;

public sealed class GameIdAliasStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(),
        "tript-game-id-aliases-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void UnknownId_ResolvesToNull()
    {
        var store = new GameIdAliasStore(_path);
        Assert.Null(store.Resolve("custom-abc"));
    }

    [Fact]
    public void AddedAlias_Resolves()
    {
        var store = new GameIdAliasStore(_path);
        Assert.True(store.TryAdd("custom-abc", "REAL01"));
        Assert.Equal("REAL01", store.Resolve("custom-abc"));
    }

    [Fact]
    public void AliasChain_ResolvesToTheFinalId()
    {
        var store = new GameIdAliasStore(_path);
        store.TryAdd("custom-abc", "custom-def");
        store.TryAdd("custom-def", "REAL01");

        Assert.Equal("REAL01", store.Resolve("custom-abc"));
    }

    [Fact]
    public void SameOldAndNewId_IsRejected()
    {
        var store = new GameIdAliasStore(_path);
        Assert.False(store.TryAdd("REAL01", "REAL01"));
        Assert.Null(store.Resolve("REAL01"));
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        var store = new GameIdAliasStore(_path);
        store.TryAdd("custom-abc", "REAL01");

        var reloaded = new GameIdAliasStore(_path);
        Assert.Equal("REAL01", reloaded.Resolve("custom-abc"));
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
