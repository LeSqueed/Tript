// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class UpdateMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "tript-update-marker-" + Guid.NewGuid().ToString("N"));

    public UpdateMarkerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void WriteAtomic_RoundTripsThroughTryRead()
    {
        var path = Path.Combine(_root, "ready.marker");
        var marker = new UpdateMarker(UpdateMarker.CurrentFormatVersion, "0.1.0-alpha.3", "staged-abc123");

        UpdateMarker.WriteAtomic(path, marker);
        var read = UpdateMarker.TryRead(path);

        Assert.Equal(marker, read);
    }

    [Fact]
    public void TryRead_ReturnsNullWhenMissing()
    {
        Assert.Null(UpdateMarker.TryRead(Path.Combine(_root, "missing.marker")));
    }

    [Fact]
    public void TryRead_ReturnsNullForWrongFormatVersion()
    {
        var path = Path.Combine(_root, "ready.marker");
        File.WriteAllText(path, "2\n0.1.0-alpha.3\nstaged-abc\n");

        Assert.Null(UpdateMarker.TryRead(path));
    }

    [Fact]
    public void TryRead_ReturnsNullForTooFewLines()
    {
        var path = Path.Combine(_root, "ready.marker");
        File.WriteAllText(path, "1\n0.1.0-alpha.3\n");

        Assert.Null(UpdateMarker.TryRead(path));
    }

    [Fact]
    public void TryRead_ReturnsNullWhenStagedFolderNameLooksUnsafe()
    {
        var path = Path.Combine(_root, "ready.marker");
        File.WriteAllText(path, "1\n0.1.0-alpha.3\n..\\..\\evil\n");

        Assert.Null(UpdateMarker.TryRead(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
