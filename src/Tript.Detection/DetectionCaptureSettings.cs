// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Serilog;
using Tript.Obs;

namespace Tript.Detection;

internal static class DetectionCaptureSettings
{
    private const int FpsDivisor = 30;
    private const int TargetCaptureFps = 3;
    private const int ObsSubscribeWidth = 1920;
    private const int ObsSubscribeHeight = 1080;

    private const int OcrSubscribeMaxWidth = 2560;
    private const int OcrSubscribeMaxHeight = 1440;

    internal static int ComputeFrameRateDivisor(int outputFps)
    {
        if (outputFps <= 0) return FpsDivisor;
        return Math.Max(1, outputFps / TargetCaptureFps);
    }

    internal static (int Width, int Height) ResolveSubscribeSize(bool hasOcr)
    {
        if (!hasOcr) return (ObsSubscribeWidth, ObsSubscribeHeight);
        try
        {
            if (FrameSourceRegistry.Current.GetVideoTiming() is { Width: > 0, Height: > 0 } timing)
            {
                return (
                    Math.Clamp((int)timing.Width, ObsSubscribeWidth, OcrSubscribeMaxWidth),
                    Math.Clamp((int)timing.Height, ObsSubscribeHeight, OcrSubscribeMaxHeight));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read OBS output size, using {W}x{H}",
                ObsSubscribeWidth, ObsSubscribeHeight);
        }
        return (ObsSubscribeWidth, ObsSubscribeHeight);
    }

    internal static int GetConfiguredOutputFps()
    {
        try
        {
            var info = FrameSourceRegistry.Current.GetVideoTiming();
            if (info == null)
            {
                Log.Warning("VisualEventDetector: OBS reported no video info, using default divisor");
                return 0;
            }

            var num = info.Value.FpsNumerator;
            var den = info.Value.FpsDenominator;
            if (num == 0 || den == 0) return 0;
            return (int)Math.Round((double)num / den);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VisualEventDetector: could not read OBS output fps, using default divisor");
            return 0;
        }
    }
}
