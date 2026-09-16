// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public interface IClipEngine
{
    IReadOnlyList<string> CreateClips(ClipRequest request);

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
