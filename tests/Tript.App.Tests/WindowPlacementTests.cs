// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;
using Tript.Shell;
using Xunit;

namespace Tript.App.Tests;

public sealed class WindowPlacementTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "tript-window-placement-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, SettingsFilePaths.WindowStateFileName);

    private WindowPlacementStore CreateStore() => new(new SettingsFileProvider(FilePath));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Sanitize_KeepsAUsablePlacement()
    {
        var saved = new WindowPlacement(-1900, 40, 1600, 900, Maximized: true);
        Assert.Equal(saved, WindowPlacement.Sanitize(saved));
    }

    [Fact]
    public void Sanitize_HasNothingToRestoreWithoutASavedPlacement()
    {
        Assert.Null(WindowPlacement.Sanitize(null));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 100, 160, 28)]
    [InlineData(100, 100, 1280, -800)]
    [InlineData(100, 100, 99999, 800)]
    [InlineData(-40000, 100, 1280, 800)]
    public void Sanitize_DiscardsADegeneratePlacement(int left, int top, int width, int height)
    {
        Assert.Null(WindowPlacement.Sanitize(new WindowPlacement(left, top, width, height, false)));
    }

    [Fact]
    public void Store_RoundTripsThePlacement()
    {
        var placement = new WindowPlacement(120, 80, 1440, 900, Maximized: true);

        CreateStore().Save(placement);

        Assert.Equal(placement, CreateStore().Load());
    }

    [Fact]
    public void Store_LoadsNothingWhenNoPlacementWasSaved()
    {
        Assert.Null(CreateStore().Load());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    public void Store_TreatsAnUnreadableFileAsNoPlacement(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, contents);

        Assert.Null(CreateStore().Load());
    }
}
