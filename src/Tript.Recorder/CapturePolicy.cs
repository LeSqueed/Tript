// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Settings;

namespace Tript.Recorder;

// What the recording scene is made of, resolved from the capture settings. The display layer is
// opt-in through the method rather than always present: it is the layer that makes an unhooked game
// capture survivable, but it is also the layer that records the desktop when the user asked for the
// game and only the game.
public sealed record CapturePolicy(
    DisplayCaptureMethod Method,
    string? PreferredDisplayId,
    TimeSpan GameCaptureTimeout)
{
    public static CapturePolicy Default { get; } = new(DisplayCaptureMethod.Auto, null, TimeSpan.FromSeconds(10));

    public bool IncludesGameCapture => Method != DisplayCaptureMethod.Display;

    public bool IncludesDisplayCapture => Method != DisplayCaptureMethod.Game;

    // Only the Game method has nothing to fall back on, so it is the only one for which an unhooked
    // capture has to end the recording rather than be logged.
    public bool StopsWhenUnhooked => Method == DisplayCaptureMethod.Game;

    public static CapturePolicy From(ResolvedRecorderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // A nonsense timeout would either end every Game recording at once or never end one; the
        // model's own default is the sane value to stand in.
        var timeout = settings.GameCaptureTimeout > TimeSpan.Zero
            ? settings.GameCaptureTimeout
            : Default.GameCaptureTimeout;

        return new CapturePolicy(settings.CaptureMethod, settings.Display, timeout);
    }
}

// Why a Game-method recording cannot show the game. Carries the deadline that passed and the
// message the host surfaces to the user.
public sealed record GameCaptureUnavailable(TimeSpan Timeout, string Message);
