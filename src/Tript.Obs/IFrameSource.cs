// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// Kept as a fraction all the way to the consumer. The video pipeline runs at rates that are not
// whole numbers — 59.94 fps is 60000/1001 — so a numerator on its own says nothing, and an
// interface exposing a single integer fps would have to round before the caller can decide how.
public readonly record struct VideoTiming(
    uint FpsNumerator, uint FpsDenominator, uint Width = 0, uint Height = 0);

// Disposing unsubscribes. Nothing else: a subscription is a lifetime, not a handle to poll.
public interface IFrameSubscription : IDisposable
{
}

public interface IFrameSource
{
    // frameRateDivisor asks for every Nth composited frame. It belongs here rather than in the
    // consumer because the frames it skips are then never read back or marshalled at all, which is
    // the entire saving — a consumer that received all of them and discarded most would have paid
    // for them first.
    IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
        FrameCallback callback, uint frameRateDivisor);

    // Null when the runtime reports no timing, which it does before the video pipeline is up.
    VideoTiming? GetVideoTiming();
}
