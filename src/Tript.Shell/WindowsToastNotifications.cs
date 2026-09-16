// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

#if WINDOWS_TOAST
using Microsoft.Toolkit.Uwp.Notifications;

namespace Tript.Shell;

// Photino's notifications use the .ico as the image, which toasts cannot load.
internal static class WindowsToastNotifications
{
    internal static void Show(string title, string body, string? iconPath)
    {
        var builder = new ToastContentBuilder().AddText(title).AddText(body);
        if (iconPath is not null && File.Exists(iconPath))
            builder.AddAppLogoOverride(new Uri(iconPath), ToastGenericAppLogoCrop.Default);
        builder.AddAudio(new ToastAudio { Silent = true });
        builder.Show();
    }
}
#endif
