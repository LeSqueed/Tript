// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// The clip pipeline: probe a finished recording, cut the marked regions at their exact times
// (re-encoding so no broken frame survives), handle colour correctly, and write the clip file(s).
// The IPC handler for CreateClip drives this; the handler is a later task.
public interface IClipEngine
{
    // Produces one or more clip files and returns their paths. Combine mode returns one path;
    // Separate mode returns one per region, in region order.
    IReadOnlyList<string> CreateClips(ClipRequest request);
}
