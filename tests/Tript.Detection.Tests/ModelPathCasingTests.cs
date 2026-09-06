// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using Xunit;

namespace Tript.Detection.Tests;

// ModelService used to build model paths with a bare Path.Combine(BasePath, gameId) and probe them
// with File.Exists, trusting whatever casing the caller passed. The shipped folder is
// data/models/Overwatch while a sanitized display name is lowercased, so on a case-sensitive
// filesystem — ext4, the Flatpak runtime — the lookup missed and the entire ML feature no-opped
// with nothing logged. Every other test in this suite passes the exact on-disk casing, so these are
// the only ones that walk FindGameDirectory.
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

    // Fail loudly rather than skip, matching InputTensorReuseTests: a guard that quietly disables
    // itself where the model is absent is worse than no guard at all.
    [Fact]
    public void ResolvedModelPath_ExistsRegardlessOfRequestedCasing()
    {
        var modelPath = ModelService.GetModelPath("57zZvAz0PjK8VqGpKb728qE57c");
        Assert.True(File.Exists(modelPath),
            $"Case-insensitive resolution produced {modelPath}, which does not exist.");
    }

    // The fallback is load-bearing: SaveEventDefinitions creates the directory for a game that has
    // none yet, so an unmatched id must still yield the literal path rather than null or a throw.
    [Fact]
    public void UnknownGameId_FallsBackToLiteralPath()
    {
        Assert.Equal(Path.Combine(ModelService.BasePath, "not-a-real-game"),
            ModelService.GetGamePath("not-a-real-game"));
        Assert.False(ModelService.HasModelForGame("not-a-real-game"));
    }
}
