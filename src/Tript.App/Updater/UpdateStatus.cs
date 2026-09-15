// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Updater;

internal enum UpdateStage
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Error,
}

internal sealed class UpdateStatusPayload
{
    public required string Stage { get; init; }

    public string? Version { get; init; }

    public string? ReleaseUrl { get; init; }

    public long? CompletedBytes { get; init; }

    public long? TotalBytes { get; init; }

    public string? Error { get; init; }
}
