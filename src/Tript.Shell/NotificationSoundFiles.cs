// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.App;

namespace Tript.Shell;

internal static class NotificationSoundFiles
{
    internal static string? PathFor(NotificationKind kind, string webRoot)
    {
        var fileName = kind switch
        {
            NotificationKind.RecordingStarted => "recording-started.wav",
            NotificationKind.RecordingStopped => "recording-stopped.wav",
            NotificationKind.Error => "error.wav",
            _ => null,
        };
        return fileName is null ? null : Path.Combine(webRoot, "sounds", fileName);
    }
}
