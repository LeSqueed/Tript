// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

public static class BuildFeatures
{
#if TRIPT_TRAINING
    public const bool TrainingEnabled = true;
#else
    public const bool TrainingEnabled = false;
#endif
}
