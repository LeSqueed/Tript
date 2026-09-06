// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Obs;
using Tript.Settings;

namespace Tript.Recorder;

public interface IRecorderSession : IDisposable
{
    IRecorderOutput CreateOutput(ResolvedRecorderSettings settings);

    void PlaceSourceOnChannel();

    void ClearSourceFromChannel();

    CapturePolicy Policy { get; }

    bool HasGameCaptureSource { get; }

    bool HasDisplayFallback { get; }

    // True when the hook attached before the deadline; false when the deadline passed or the token
    // was cancelled. warningAfter <= zero means no warning.
    bool WaitForGameCapture(TimeSpan deadline, TimeSpan warningAfter, Action showWarning,
        Action clearWarning, CancellationToken cancellationToken);

    new void Dispose();
}
