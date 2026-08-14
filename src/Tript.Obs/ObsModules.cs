// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

// obs_open_module's return codes. MODULE_FILE_NOT_FOUND is a deprecated alias for FailedToOpen and
// carries the same value, so it gets no member of its own.
public enum ObsModuleOpenResult
{
    Success = 0,
    Error = -1,
    FailedToOpen = -2,
    MissingExports = -3,
    IncompatibleVersion = -4,
    HardcodedSkip = -5
}

// An obs_module_t*. Borrowed, not owned: libobs keeps every module it opens until obs_shutdown and
// exposes no way to close one, so this is a struct rather than a handle — there is nothing to
// release and nothing to leak.
public readonly record struct ObsModule(nint Pointer)
{
    public bool IsValid => Pointer != nint.Zero;
}

// What obs_load_all_modules2 reports. A module that fails to load is not an error for the process —
// the DeckLink plugin fails on any machine without the drivers — but it is the difference between
// an encoder being missing and an encoder being broken, so it is surfaced rather than dropped.
public sealed record ObsModuleLoadReport(IReadOnlyList<string> FailedModules)
{
    public bool AllLoaded => FailedModules.Count == 0;
}
