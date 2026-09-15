// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Updater;
using Xunit;

namespace Tript.App.Tests;

public sealed class Sha256SidecarTests
{
    private static readonly string ValidHex = new('a', 64);

    [Fact]
    public void TryParse_AcceptsStandardSha256SumFormat()
    {
        var content = $"{ValidHex}  Tript-0.1.0-alpha.3-win-x64.zip\n";
        Assert.Equal(ValidHex, Sha256Sidecar.TryParse(content));
    }

    [Fact]
    public void TryParse_AcceptsCrLfLineEndings()
    {
        var content = $"{ValidHex}  Tript-0.1.0-alpha.3-win-x64.zip\r\n";
        Assert.Equal(ValidHex, Sha256Sidecar.TryParse(content));
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveAndNormalizesToLowercase()
    {
        var content = $"{new string('A', 64)}  file.zip";
        Assert.Equal(ValidHex, Sha256Sidecar.TryParse(content));
    }

    [Fact]
    public void TryParse_RejectsEmptyContent()
    {
        Assert.Null(Sha256Sidecar.TryParse(string.Empty));
    }

    [Fact]
    public void TryParse_RejectsNull()
    {
        Assert.Null(Sha256Sidecar.TryParse(null));
    }

    [Fact]
    public void TryParse_RejectsShortHexToken()
    {
        var content = new string('a', 63) + "  file.zip";
        Assert.Null(Sha256Sidecar.TryParse(content));
    }

    [Fact]
    public void TryParse_RejectsNonHexCharacters()
    {
        var content = new string('g', 64) + "  file.zip";
        Assert.Null(Sha256Sidecar.TryParse(content));
    }
}
