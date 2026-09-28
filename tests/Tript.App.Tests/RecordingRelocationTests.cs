// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class RecordingRelocationTests : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private static readonly TimeSpan[] NoWait = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tript-relocation-" + Guid.NewGuid().ToString("N"));

    public RecordingRelocationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private (string Source, string Destination) Planned(string name)
    {
        var source = Path.Combine(_root, "sessions", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, name);
        return (source, Path.Combine(_root, "Wardogs", "sessions", name));
    }

    [Fact]
    public void AShortLivedLock_IsRetriedUntilTheMoveSucceeds()
    {
        var move = Planned("session.mp4");
        var failuresLeft = 2;

        var moved = RecordingRelocation.TryMoveAll([move], (source, destination) =>
        {
            if (failuresLeft-- > 0) throw new IOException("in use", SharingViolation);
            File.Move(source, destination);
        }, NoWait, out var failure);

        Assert.True(moved);
        Assert.Null(failure);
        Assert.True(File.Exists(move.Destination));
        Assert.False(File.Exists(move.Source));
    }

    [Fact]
    public void APersistentLock_FailsAndRollsEarlierMovesBack()
    {
        var session = Planned("session.mp4");
        var highlight = Planned("session-highlight.mp4");

        var moved = RecordingRelocation.TryMoveAll([session, highlight], (source, destination) =>
        {
            if (source == highlight.Source) throw new IOException("in use", SharingViolation);
            File.Move(source, destination);
        }, NoWait, out var failure);

        Assert.False(moved);
        Assert.IsType<IOException>(failure);
        Assert.True(File.Exists(session.Source));
        Assert.False(File.Exists(session.Destination));
        Assert.True(File.Exists(highlight.Source));
    }

    [Fact]
    public void AnErrorThatIsNotALock_FailsWithoutRetrying()
    {
        var move = Planned("session.mp4");
        var attempts = 0;

        var moved = RecordingRelocation.TryMoveAll([move], (_, _) =>
        {
            attempts++;
            throw new IOException("disk full");
        }, NoWait, out _);

        Assert.False(moved);
        Assert.Equal(1, attempts);
        Assert.True(File.Exists(move.Source));
    }
}
