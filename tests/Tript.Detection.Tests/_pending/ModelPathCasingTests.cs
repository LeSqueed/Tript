// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Detection;
using ReferenceProduct.Backend.Games;
using Xunit;

namespace Tript.Detection.Tests;

// The rest of this file's tests now run in ../ModelPathCasingTests.cs. What is left is the one that
// needs the Stage 2 integration subsystem.
public class ModelPathCasingIntegrationTests
{
    // End-to-end across the id resolution and the path resolution: the name-only path that a
    // catalog lookup without an IGDB id takes has to reach the shipped model on Linux.
    [Fact]
    public void SanitizedDisplayName_ReachesTheShippedModel()
    {
        var modelId = GameIntegrationService.ResolveModelId(null, "Overwatch");
        Assert.NotNull(modelId);
        Assert.True(ModelService.HasModelForGame(modelId!),
            $"Resolved id '{modelId}' found no model under {ModelService.BasePath}.");
    }
}
