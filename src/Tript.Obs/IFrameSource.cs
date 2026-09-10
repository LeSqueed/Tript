// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public readonly record struct VideoTiming(
    uint FpsNumerator, uint FpsDenominator, uint Width = 0, uint Height = 0);

public interface IFrameSubscription : IDisposable
{
}

public interface IFrameSource
{
    IFrameSubscription Subscribe(FramePixelFormat format, int width, int height,
        FrameCallback callback, uint frameRateDivisor);

    VideoTiming? GetVideoTiming();
}
