// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class ShellExitRequestTests
{
    [Theory]
    [InlineData("--exit")]
    [InlineData("--startup", "--exit")]
    [InlineData("--exit", "--fake-recorder")]
    public void ExitArgument_IsRecognisedWhereverItAppears(params string[] args)
    {
        Assert.True(Tript.Shell.Program.IsExitRequest(args));
    }

    [Theory]
    [InlineData]
    [InlineData("--startup")]
    [InlineData("--exitt")]
    [InlineData("--EXIT")]
    public void OtherArguments_AreNotAnExitRequest(params string[] args)
    {
        Assert.False(Tript.Shell.Program.IsExitRequest(args));
    }

    [Fact]
    public void AppOptions_RejectsTheExitArgument()
    {
        Assert.Null(Tript.App.AppOptions.Parse([Tript.Shell.Program.ExitArgument]));
    }
}
