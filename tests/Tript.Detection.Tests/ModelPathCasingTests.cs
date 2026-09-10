// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

public class ModelPathCasingTests
{
    [Fact]
    public void LowercasedGameId_FindsShippedModel()
    {
        Assert.True(ModelService.HasModelForGame("57zzvaz0pjk8vqgpkb728qe57c"),
            $"Lowercased id found no model under {ModelService.BasePath}.");
    }

    [Fact]
    public void MixedCaseGameId_ResolvesToOnDiskCasing()
    {
        Assert.Equal("57ZZVAZ0PJK8VQGPKB728QE57C",
            Path.GetFileName(ModelService.GetGamePath("57zZvAz0PjK8VqGpKb728qE57c")));
    }

    [Fact]
    public void ResolvedModelPath_ExistsRegardlessOfRequestedCasing()
    {
        var modelPath = ModelService.GetModelPath("57zZvAz0PjK8VqGpKb728qE57c");
        Assert.True(File.Exists(modelPath),
            $"Case-insensitive resolution produced {modelPath}, which does not exist.");
    }

    [Fact]
    public void UnknownGameId_FallsBackToLiteralPath()
    {
        Assert.Equal(Path.Combine(ModelService.BasePath, "not-a-real-game"),
            ModelService.GetGamePath("not-a-real-game"));
        Assert.False(ModelService.HasModelForGame("not-a-real-game"));
    }
}
