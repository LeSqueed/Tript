// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

// libobs is one global context per process and is not re-entrant: one video mix, one audio mix, one
// log handler. Two tests running at once would not be testing the same library twice, they would be
// fighting over one. Parallelism is off for the whole assembly rather than per collection, because
// a second collection added later would otherwise reintroduce it silently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
