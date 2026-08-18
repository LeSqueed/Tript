// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Ipc;
using Xunit;

namespace Tript.App.Tests;

// The three loopback ports. The control socket's Origin allowlist is the app's only authentication,
// and it used to name the UI host's port as a literal string while UiHost owned it as a private
// const — so moving the UI port locked the app out of its own socket: 403 at the handshake, and a UI
// that reconnects forever. These tests pin the derivation, not the numbers.
//
// IpcOriginTests covers which origins the allowlist accepts; this covers where the allowlist comes
// from.
public class LocalPortsTests
{
    [Fact]
    public void TheOriginAllowlist_IsDerivedFromTheUiPort()
    {
        Assert.Equal(
            new[] { $"http://localhost:{LocalPorts.Ui}", $"http://127.0.0.1:{LocalPorts.Ui}" },
            LocalPorts.UiOrigins);
    }

    // The consequence, stated the way it bites: whatever the UI port is, the socket accepts a page
    // served from it.
    [Theory]
    [InlineData("http://localhost:{0}")]
    [InlineData("http://127.0.0.1:{0}")]
    public void APageServedByTheUiHost_IsAlwaysAccepted(string template) =>
        Assert.True(IpcServer.IsAllowedOrigin(string.Format(template, LocalPorts.Ui)));

    [Fact]
    public void TheThreePorts_AreDistinct()
    {
        var ports = new[] { LocalPorts.Ui, LocalPorts.Content, LocalPorts.ControlSocket };
        Assert.Equal(ports.Length, ports.Distinct().Count());
    }

    // The frontend cannot share a constant across the language boundary, so its copies are pinned
    // here instead. A port moved on one side and not the other is a UI that cannot reach the app.
    [SkippableFact]
    public void TheFrontendsCopies_MatchTheseConstants()
    {
        var endpoints = RepoFile("src/Tript.Web/src/ipc/endpoints.ts");
        Skip.If(endpoints is null, "the frontend sources are not next to this test run");

        var text = File.ReadAllText(endpoints!);
        Assert.Contains($"ws://localhost:{LocalPorts.ControlSocket}/", text, StringComparison.Ordinal);
        Assert.Contains($"http://localhost:{LocalPorts.Content}/", text, StringComparison.Ordinal);
    }

    // Walks up from the test assembly looking for the repository layout. Returns null when the test
    // runs from a package rather than the tree.
    private static string? RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(LocalPortsTests).Assembly.Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}
