// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

internal static class GameCaptureWait
{
    internal static readonly TimeSpan LaunchedForCaptureLimit = TimeSpan.FromMinutes(2);

    internal static TimeSpan? Deadline(bool hasDisplayFallback, TimeSpan policyTimeout, bool? captureLayerLoaded) =>
        (hasDisplayFallback, captureLayerLoaded) switch
        {
            (false, _) => Timeout.InfiniteTimeSpan,
            (true, false) => null,
            (true, true) => policyTimeout > LaunchedForCaptureLimit ? policyTimeout : LaunchedForCaptureLimit,
            (true, null) => policyTimeout,
        };
}
