// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell;

internal static class ShellExitCodes
{
    // Mirrored as TRIPT_EXIT_CODE_RESTART_FOR_UPDATE in src/Tript.Launcher/launcher.c - keep the
    // two in step.
    internal const int RestartForUpdate = 90;

    // `Tript.Shell.exe --exit` asked a running instance to quit and it was still there when the
    // wait ran out. The launcher passes this straight through, so a deploy script can tell it apart
    // from a clean shutdown and refuse to overwrite the files.
    internal const int ExitRequestTimedOut = 91;
}
