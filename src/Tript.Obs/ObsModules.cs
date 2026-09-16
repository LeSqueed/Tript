// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public enum ObsModuleOpenResult
{
    Success = 0,
    Error = -1,
    FailedToOpen = -2,
    MissingExports = -3,
    IncompatibleVersion = -4,
    HardcodedSkip = -5
}

// Borrowed: libobs keeps every module until obs_shutdown and cannot close one.
public readonly record struct ObsModule(nint Pointer)
{
    public bool IsValid => Pointer != nint.Zero;
}

public sealed record ObsModuleLoadReport(IReadOnlyList<string> FailedModules)
{
    public bool AllLoaded => FailedModules.Count == 0;
}
