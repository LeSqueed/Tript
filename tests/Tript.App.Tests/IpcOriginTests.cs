// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Ipc;
using Xunit;

namespace Tript.App.Tests;

public class IpcOriginTests
{
    [Theory]
    [InlineData("http://localhost:8892")]
    [InlineData("http://127.0.0.1:8892")]
    [InlineData("HTTP://LOCALHOST:8892")]
    public void TheUiHostsOwnOrigin_IsAccepted(string origin) =>
        Assert.True(IpcServer.IsAllowedOrigin(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoOriginAtAll_IsAccepted(string? origin) =>
        Assert.True(IpcServer.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://localhost:8892.evil.com")]
    [InlineData("http://localhost:28892")]
    [InlineData("null")]
    public void AnyOtherOrigin_IsRefused(string origin) =>
        Assert.False(IpcServer.IsAllowedOrigin(origin));
}
