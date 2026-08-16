// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tript.App;

// The alpha has no window of its own: it serves its UI over HTTP (spec/local-ipc.md) and the
// browser is the frontend. Opening that browser is the last step of startup, so a build that just
// prints READY and sits there is not a broken build — but launching the page by hand every time is
// the kind of friction that reads as one. So after READY the host asks the platform's default
// browser to open the UI, best-effort: on a headless box or under a smoke test there is no browser
// to opencars and the host must not fail (or pause) because of it.
internal static class BrowserLauncher
{
    internal static void Open(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
                return;
            }

            // Linux: xdg-open is the freedesktop way to launch the default browser. It can return
            // before the browser is up; that is fine, the UI is already being served.
            Process.Start("xdg-open", url);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser, no xdg-open, or the process could not be started. The UI is still served
            // at the URL; the user can open it by hand.
        }
    }
}
