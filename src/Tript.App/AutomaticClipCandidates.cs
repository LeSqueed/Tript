// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using Tript.Core;

namespace Tript.App;

internal static class AutomaticClipCandidates
{
    internal static bool Includes(Bookmark bookmark) =>
        bookmark.IsAutomaticClipCandidate == true
        || (bookmark.IsAutomaticClipCandidate is null && bookmark.Type.IsIncludedInHighlights());
}
