// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.RegularExpressions;
using Xunit;

namespace Tript.App.Tests;

public sealed class ClientErrorBridgeTests
{
    // The prefix is hand-mirrored between the page and the shell. If they drift, UI errors are not
    // logged and nothing says so: the shell simply ignores a message it does not recognise.
    [Fact]
    public void TheShellListensForThePrefixThePageSends()
    {
        var source = File.ReadAllText(FindRepoFile(Path.Combine("src", "Tript.Web", "src", "app", "errorReporting.ts")));
        var match = Regex.Match(source, @"CLIENT_ERROR_PREFIX\s*=\s*'([^']+)'");

        Assert.True(match.Success, "CLIENT_ERROR_PREFIX was not found in errorReporting.ts");
        Assert.Equal(Tript.Shell.ShellWindow.ClientErrorPrefix, match.Groups[1].Value);
    }

    private static string FindRepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate) && File.Exists(Path.Combine(directory.FullName, "Tript.slnx")))
                return candidate;
        }

        throw new FileNotFoundException($"{relative} was not found above the test binary.");
    }
}
