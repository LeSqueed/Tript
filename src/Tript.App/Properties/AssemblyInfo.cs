// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Runtime.CompilerServices;

// The desktop shell (src/Tript.Shell) reuses the app host's exact construction path — the same
// BuildApp seam the headless launcher uses — so a windowed build and a browser build are the same
// host, not two divergent copies. Exposing the internal construction seam is all the shell needs;
// nothing else about the host is public.
[assembly: InternalsVisibleTo("Tript.Shell")]
