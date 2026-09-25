// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

public sealed record CapturePolicy(
    DisplayCaptureMethod Method,
    string? PreferredDisplayId,
    TimeSpan GameCaptureTimeout)
{
    public static CapturePolicy Default { get; } = new(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(10));

    public bool IncludesGameCapture => Method != DisplayCaptureMethod.Display;

    public bool IncludesDisplayCapture => Method != DisplayCaptureMethod.Game;

    public static CapturePolicy From(ResolvedRecorderSettings settings, bool gameCaptureAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var timeout = settings.GameCaptureTimeout > TimeSpan.Zero
            ? settings.GameCaptureTimeout
            : Default.GameCaptureTimeout;

        var method = settings.CaptureMethod == DisplayCaptureMethod.Game && !gameCaptureAvailable
            ? DisplayCaptureMethod.Display
            : settings.CaptureMethod;

        return new CapturePolicy(method, settings.Display, timeout);
    }
}
