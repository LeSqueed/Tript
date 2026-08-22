// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Core;

// Compile-time feature switches shared by the host and its libraries. Training is deliberately not
// a runtime setting: a normal build must not carry an activatable training surface.
public static class BuildFeatures
{
#if TRIPT_TRAINING
    public const bool TrainingEnabled = true;
#else
    public const bool TrainingEnabled = false;
#endif
}
