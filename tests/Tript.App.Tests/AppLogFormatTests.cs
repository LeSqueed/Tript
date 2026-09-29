// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace Tript.App.Tests;

public sealed class AppLogFormatTests
{
    private static LogEvent Event(string template, params (string Name, object? Value)[] properties) =>
        new(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse(template),
            properties.Select(property => new LogEventProperty(property.Name, new ScalarValue(property.Value))));

    [Fact]
    public void RenderMessage_WritesStringsWithoutQuotes()
    {
        var logEvent = Event("dispatching {Method} for {Game}", ("Method", "ListGames"), ("Game", "Overwatch 2"));

        Assert.Equal("dispatching ListGames for Overwatch 2", AppLog.RenderMessage(logEvent));
    }

    [Fact]
    public void RenderMessage_FormatsNonStringValuesInvariantly()
    {
        var logEvent = Event("took {Seconds:0.00}s, {Count} frames", ("Seconds", 1.5), ("Count", 1200));

        Assert.Equal("took 1.50s, 1200 frames", AppLog.RenderMessage(logEvent));
    }

    [Fact]
    public void RenderMessage_LeavesAMissingPropertyAsItsPlaceholder()
    {
        var logEvent = Event("model {Model} failed");

        Assert.Equal("model {Model} failed", AppLog.RenderMessage(logEvent));
    }
}
