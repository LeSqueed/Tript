// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Obs;

public sealed record ObsModuleLoadReport(IReadOnlyList<string> FailedModules)
{
    public bool AllLoaded => FailedModules.Count == 0;
}
