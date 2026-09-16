// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell;

internal static class ShellExitCodes
{
    // Mirrored as TRIPT_EXIT_CODE_RESTART_FOR_UPDATE in src/Tript.Launcher/launcher.c.
    internal const int RestartForUpdate = 90;

    // Passed through by the launcher so deploy scripts can refuse to overwrite a running app.
    internal const int ExitRequestTimedOut = 91;
}
