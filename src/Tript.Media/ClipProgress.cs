// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

// A single progress observation from the clip pipeline. Stages are coarse and deliberately not an
// enum: the pipeline reports the stage it is in as free text ("probing", "extracting region 2/3",
// "converting colour"), mirroring how the training surface streams status plus a raw line.
public readonly record struct ClipProgress(string Stage, string? Detail = null)
{
    public static ClipProgress At(string stage, string? detail = null) => new(stage, detail);
}
