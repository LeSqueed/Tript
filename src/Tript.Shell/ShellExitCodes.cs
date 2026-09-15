// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Shell;

internal static class ShellExitCodes
{
    // Mirrored as TRIPT_EXIT_CODE_RESTART_FOR_UPDATE in src/Tript.Launcher/launcher.c - keep the
    // two in step.
    internal const int RestartForUpdate = 90;
}
