// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class SdrOutputReservationsTests : IDisposable
{
    private readonly string _root;
    private readonly SdrOutputReservations _reservations = new();

    public SdrOutputReservationsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tript-sdr-reservations", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void TryReserve_RefusesASecondConversionOfTheSameSource()
    {
        var source = Path.Combine(_root, "clip.mp4");

        Assert.True(_reservations.TryReserve(source, out var output));
        Assert.Equal(Path.Combine(_root, "clip-sdr.mp4"), output);
        Assert.False(_reservations.TryReserve(source.ToUpperInvariant(), out _));

        _reservations.Release(source, output);
        Assert.True(_reservations.TryReserve(source, out _));
    }

    [Fact]
    public void TryReserve_SkipsExistingAndReservedOutputs()
    {
        File.WriteAllText(Path.Combine(_root, "clip-sdr.mp4"), "taken");
        var first = Path.Combine(_root, "clip.mp4");
        var second = Path.Combine(_root, "clip.mov");

        Assert.True(_reservations.TryReserve(first, out var firstOutput));
        Assert.True(_reservations.TryReserve(second, out var secondOutput));

        Assert.Equal(Path.Combine(_root, "clip-sdr-2.mp4"), firstOutput);
        Assert.Equal(Path.Combine(_root, "clip-sdr-3.mp4"), secondOutput);
    }
}
