// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if WINDOWS_TOAST
using System.Runtime.InteropServices;
using Tript.App;

namespace Tript.Shell;

// Plays the notification cue directly through winmm — no toast involvement, no new NuGet
// dependency — so it stays independent of whether the toast itself is shown or silenced.
internal static class NativeSound
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SndFilename = 0x00020000;
    private const uint SndAsync = 0x00000001;
    private const uint SndNodefault = 0x00000002;

    internal static void Play(NotificationKind kind, string webRoot)
    {
        var fileName = kind switch
        {
            NotificationKind.RecordingStarted => "recording-started.wav",
            NotificationKind.RecordingStopped => "recording-stopped.wav",
            NotificationKind.Error => "error.wav",
            _ => null,
        };
        if (fileName is null)
            return;

        var path = Path.Combine(webRoot, "sounds", fileName);
        if (File.Exists(path))
            PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNodefault);
    }
}
#endif
