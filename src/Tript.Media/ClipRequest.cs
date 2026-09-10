// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Media;

public sealed class ClipRequest
{
    public string OperationId { get; init; } = string.Empty;

    public required string SourcePath { get; init; }

    public string? SourceSessionPath { get; init; }

    public required IReadOnlyList<ClipRegion> Regions { get; init; }

    public required ClipMode Mode { get; init; }

    public required string OutputPath { get; init; }

    public IReadOnlyList<AudioTrackAdjustment> AudioTrackAdjustments { get; init; } = [];

    public string EncoderFamily { get; init; } = "libx265";

    public bool ForceSdr { get; set; }

    public bool PreferStreamCopy { get; init; }

    public string Title { get; init; } = string.Empty;

    public Action<ClipProgress>? Progress { get; init; }
}
