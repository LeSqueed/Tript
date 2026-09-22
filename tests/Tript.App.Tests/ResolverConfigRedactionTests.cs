// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App.Resolver;
using Xunit;

namespace Tript.App.Tests;

public sealed class ResolverConfigRedactionTests
{
    // Logs are what a user is asked to send when something breaks. A record's generated ToString
    // prints every positional member, so without the override the key would land in any log line or
    // exception message that formats the config.
    [Fact]
    public void FormattingTheConfigNeverPrintsTheApiKey()
    {
        const string key = "b4954f42-secret-api-key";
        var config = new ResolverConfig(new Uri("https://resolver.example"), key);

        Assert.DoesNotContain(key, config.ToString());
        Assert.DoesNotContain(key, $"{config}");
        Assert.Contains("resolver.example", config.ToString());
    }

    [Fact]
    public void AMissingKeyIsReportedAsAbsent() =>
        Assert.Contains("ApiKey = none", new ResolverConfig(new Uri("https://resolver.example"), null).ToString());
}
