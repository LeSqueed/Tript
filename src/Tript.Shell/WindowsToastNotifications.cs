// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if WINDOWS_TOAST
using Microsoft.Toolkit.Uwp.Notifications;

namespace Tript.Shell;

// Photino's own SendNotification reuses whatever file is set as the window icon (tript.ico) as the
// toast's image, but Windows toast images only support PNG/JPG/GIF — the load silently fails,
// leaving an empty slot in the fixed legacy template Photino always uses. This sends a real,
// modern toast directly instead, with full control over the icon and layout.
internal static class WindowsToastNotifications
{
    internal static void Show(string title, string body, string? iconPath)
    {
        var builder = new ToastContentBuilder().AddText(title).AddText(body);
        if (iconPath is not null && File.Exists(iconPath))
            builder.AddAppLogoOverride(new Uri(iconPath), ToastGenericAppLogoCrop.Default);
        // The custom cue is played separately via NativeSound — the toast itself always stays
        // silent so Windows' own default "ding" never layers on top of it.
        builder.AddAudio(new ToastAudio { Silent = true });
        builder.Show();
    }
}
#endif
