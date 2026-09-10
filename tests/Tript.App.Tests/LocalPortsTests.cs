// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Ipc;
using Xunit;

namespace Tript.App.Tests;

public class LocalPortsTests
{
    [Fact]
    public void TheOriginAllowlist_IsDerivedFromTheUiPort()
    {
        Assert.Equal(
            new[] { $"http://localhost:{LocalPorts.Ui}", $"http://127.0.0.1:{LocalPorts.Ui}" },
            LocalPorts.UiOrigins);
    }

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

    [SkippableFact]
    public void TheFrontendsCopies_MatchTheseConstants()
    {
        var endpoints = RepoFile("src/Tript.Web/src/ipc/endpoints.ts");
        Skip.If(endpoints is null, "the frontend sources are not next to this test run");

        var text = File.ReadAllText(endpoints!);
        Assert.Contains($"ws://localhost:{LocalPorts.ControlSocket}/", text, StringComparison.Ordinal);
        Assert.Contains($"http://localhost:{LocalPorts.Content}/", text, StringComparison.Ordinal);
    }

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
