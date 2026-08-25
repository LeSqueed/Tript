// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Ipc;
using Xunit;

namespace Tript.App.Tests;

// The control socket's only authentication. Browsers do not apply CORS to a WebSocket handshake, so
// any page open in the user's browser can reach ws://localhost:8894 — and the commands behind it
// delete recordings, empty the trash and move the output directory.
public class IpcOriginTests
{
    [Theory]
    [InlineData("http://localhost:8892")]
    [InlineData("http://127.0.0.1:8892")]
    [InlineData("HTTP://LOCALHOST:8892")]
    public void TheUiHostsOwnOrigin_IsAccepted(string origin) =>
        Assert.True(IpcServer.IsAllowedOrigin(origin));

    // A non-browser client (the native shell's webview, this test suite's own ClientWebSocket) sends
    // no Origin at all. Refusing those would lock the app out of itself.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoOriginAtAll_IsAccepted(string? origin) =>
        Assert.True(IpcServer.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]          // another local dev server is still not us
    [InlineData("http://localhost:8892.evil.com")] // the prefix trap
    [InlineData("http://localhost:28892")]         // the port-prefix trap
    [InlineData("null")]                           // a file:// or sandboxed document
    public void AnyOtherOrigin_IsRefused(string origin) =>
        Assert.False(IpcServer.IsAllowedOrigin(origin));
}
