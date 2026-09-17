// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Recorder.Tests;

public sealed class ProcessPathCacheTests
{
    private static readonly DateTimeOffset Started = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameProcess_ReadsItsPathOnce()
    {
        var cache = new ProcessPathCache();
        var reads = 0;

        for (var tick = 0; tick < 5; tick++)
        {
            Assert.Equal(@"C:\Games\game.exe", cache.Resolve(42, Started, () =>
            {
                reads++;
                return @"C:\Games\game.exe";
            }));
        }

        Assert.Equal(1, reads);
    }

    [Fact]
    public void AReusedProcessId_WithANewStartTime_IsReadAgain()
    {
        var cache = new ProcessPathCache();
        cache.Resolve(42, Started, () => @"C:\Games\old.exe");

        var path = cache.Resolve(42, Started.AddMinutes(5), () => @"C:\Games\new.exe");

        Assert.Equal(@"C:\Games\new.exe", path);
    }

    [Fact]
    public void ADifferentProcess_IsReadAgain()
    {
        var cache = new ProcessPathCache();
        cache.Resolve(42, Started, () => @"C:\Games\first.exe");

        Assert.Equal(@"C:\Games\second.exe", cache.Resolve(43, Started, () => @"C:\Games\second.exe"));
    }

    [Fact]
    public void AnUnreadablePath_IsNotCached()
    {
        var cache = new ProcessPathCache();
        var reads = 0;

        Assert.Null(cache.Resolve(42, Started, () =>
        {
            reads++;
            return null;
        }));
        Assert.Equal(@"C:\Games\game.exe", cache.Resolve(42, Started, () =>
        {
            reads++;
            return @"C:\Games\game.exe";
        }));

        Assert.Equal(2, reads);
    }
}
