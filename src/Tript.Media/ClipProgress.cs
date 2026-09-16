// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public readonly record struct ClipProgress(string Stage, string? Detail = null)
{
    public static ClipProgress At(string stage, string? detail = null) => new(stage, detail);
}
