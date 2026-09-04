// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.App.Tests;

public sealed class DownloadedModelActivationTests
{
    [Fact]
    public void Replacement_stops_matching_active_detection_before_model_invalidation()
    {
        var modelReferenced = true;
        var restarted = false;

        AppHost.ActivateDownloadedModelCore("manual-model", "manual-model",
            () => modelReferenced = false,
            () => Assert.False(modelReferenced, "The active detector still references the model."),
            gameId => restarted = gameId == "manual-model");

        Assert.True(restarted);
    }

    [Fact]
    public void Replacement_does_not_stop_detection_for_another_model()
    {
        var stopped = false;

        AppHost.ActivateDownloadedModelCore("downloaded", "manually-active",
            () => stopped = true, () => { }, _ => true);

        Assert.False(stopped);
    }
}
