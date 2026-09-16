// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Content;
using Xunit;

namespace Tript.App.Tests;

public sealed class SharingViolationTests : IDisposable
{
    private readonly string _root;

    public SharingViolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-app-tests", nameof(SharingViolationTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "video");
        return path;
    }

    private static FileStream OpenWithoutDeleteSharing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    [SkippableFact]
    public void Run_WaitsOutAShortLock()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "only Windows refuses to move a file another handle has open");
        var source = WriteFile("brief.mp4");
        var destination = Path.Combine(_root, "moved.mp4");

        var reader = OpenWithoutDeleteSharing(source);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await reader.DisposeAsync();
        });

        SharingViolationRetry.Run(() => File.Move(source, destination),
            [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)]);
        release.Wait();

        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(source));
    }

    [SkippableFact]
    public void Run_GivesUpOnALockThatStays()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "only Windows refuses to move a file another handle has open");
        var source = WriteFile("held.mp4");
        var destination = Path.Combine(_root, "never.mp4");
        using var reader = OpenWithoutDeleteSharing(source);

        var exception = Assert.Throws<IOException>(() => SharingViolationRetry.Run(
            () => File.Move(source, destination), [TimeSpan.FromMilliseconds(10)]));

        Assert.True(SharingViolationRetry.IsSharingViolation(exception));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Run_DoesNotRetryOtherFailures()
    {
        var attempts = 0;

        Assert.Throws<FileNotFoundException>(() => SharingViolationRetry.Run(() =>
        {
            attempts++;
            throw new FileNotFoundException("gone");
        }, [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)]));

        Assert.Equal(1, attempts);
    }

    [SkippableFact]
    public void TrashAdd_WithTheVideoHeldOpen_LeavesItInPlaceWithoutCopyingIt()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "only Windows refuses to move a file another handle has open");
        var source = WriteFile("in-use.mp4");
        var trash = new TrashStore(Path.Combine(_root, ".trash"));
        using var reader = OpenWithoutDeleteSharing(source);

        var entry = trash.Add([new TrashedFile(source, "highlights/in-use.mp4")],
            new TrashEntryRecord { FileName = "in-use.mp4" }, out var failure);

        Assert.Null(entry);
        Assert.NotNull(failure);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.Exists(Path.Combine(_root, ".trash"))
            ? Directory.GetFiles(Path.Combine(_root, ".trash"), "*", SearchOption.AllDirectories)
            : []);
    }
}
