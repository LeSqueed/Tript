// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Xunit;

namespace Tript.Detection.Tests;

// A bookmark is only observable through the active recording, which is process-wide: two test
// classes swapping that recording in parallel would each count the other's bookmarks. Every class
// that installs a recording to watch what a detection cycle writes joins this collection so they
// run one at a time.
[CollectionDefinition(Name)]
public class RecordingStateCollection
{
    public const string Name = "active recording";
}
