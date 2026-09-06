// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;
using Xunit;

namespace Tript.App.Tests;

public sealed class UlidTests
{
    [Fact]
    public void Derive_MatchesTheResolverBuiltinOverwatchId()
        => Assert.Equal("57ZZVAZ0PJK8VQGPKB728QE57C", Ulid.Derive("builtin:Overwatch"));

    [Fact]
    public void Derive_IsDeterministic()
        => Assert.Equal(Ulid.Derive("igdb:25076"), Ulid.Derive("igdb:25076"));

    [Fact]
    public void Derive_ProducesA26CharCrockfordSegment()
    {
        var id = Ulid.Derive("steam:1174180");
        Assert.Equal(26, id.Length);
        Assert.Matches("^[0-9A-Z]{26}$", id);
        Assert.DoesNotMatch("[ILOU]", id);
    }

    [Fact]
    public void New_IsWellFormedAndUnique()
    {
        var a = Ulid.New();
        var b = Ulid.New();
        Assert.Equal(26, a.Length);
        Assert.Equal(26, b.Length);
        Assert.Matches("^[0-9A-Z]{26}$", a);
        Assert.DoesNotMatch("[ILOU]", a);
        Assert.NotEqual(a, b);
    }
}
