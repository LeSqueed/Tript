// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class AppOptionsTests
{
    [Fact]
    public void Parse_ReadsExplicitPorts()
    {
        var options = AppOptions.Parse(["--ui-port", "9001", "--content-port", "9002", "--control-port", "9003"]);

        Assert.NotNull(options);
        Assert.Equal(9001, options.UiPort);
        Assert.Equal(9002, options.ContentPort);
        Assert.Equal(9003, options.ControlPort);
    }

    [Theory]
    [InlineData("--ui-port", "abc")]
    [InlineData("--content-port", "-1")]
    [InlineData("--control-port", "80.5")]
    public void Parse_RefusesAPortThatIsNotANumber(string option, string value)
    {
        var error = new StringWriter();

        Assert.Null(AppOptions.Parse([option, value, "--fake-recorder"], error));

        Assert.Contains($"invalid value '{value}' for {option}", error.ToString());
        Assert.DoesNotContain("unknown argument", error.ToString());
    }

    [Fact]
    public void Parse_RefusesAPortOutOfRange()
    {
        Assert.Throws<ArgumentException>(() => AppOptions.Parse(["--ui-port", "70000"]));
    }
}
