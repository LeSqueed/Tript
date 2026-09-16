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

    // The same outputs, each paired with the regions actually cut into it after clamping. Callers
    // that need to map source times into clip times must use this: clamping can drop a region, so
    // the request's own region list cannot be zipped against the outputs.
    IReadOnlyList<ClipOutput> CreateClipOutputs(ClipRequest request)
    {
        var paths = CreateClips(request);
        var regions = request.Regions;
        return paths
            .Select((path, index) => new ClipOutput(path,
                paths.Count == regions.Count ? [regions[index]] : regions))
            .ToList();
    }
}

public sealed record ClipOutput(string Path, IReadOnlyList<ClipRegion> Regions);
