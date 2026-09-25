// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if WINDOWS_TOAST
using System.Runtime.InteropServices;
using Tript.App;

namespace Tript.Shell;

internal static class NativeSound
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    private const uint SndFilename = 0x00020000;
    private const uint SndAsync = 0x00000001;
    private const uint SndNodefault = 0x00000002;

    internal static void Play(NotificationKind kind, string webRoot)
    {
        var path = NotificationSoundFiles.PathFor(kind, webRoot);
        if (path is not null && File.Exists(path))
            PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNodefault);
    }
}
#endif
