// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App.Models;

internal sealed class GameModelManifest
{
    public int SchemaVersion { get; init; }

    public List<GameModelManifestGame> Games { get; init; } = [];
}

internal sealed class GameModelManifestGame
{
    public string GameId { get; init; } = string.Empty;

    public List<GameModelRelease> Releases { get; init; } = [];
}

internal sealed class GameModelRelease
{
    public int ModelApiVersion { get; init; }

    public int Revision { get; init; }

    public string? MinimumAppVersion { get; init; }

    public string Url { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class GameModelPackageMetadata
{
    public int PackageFormatVersion { get; init; }

    public string GameId { get; init; } = string.Empty;

    public int ModelApiVersion { get; init; }

    public int Revision { get; init; }

    public Dictionary<string, GameModelPackageFile> Files { get; init; } =
        new(StringComparer.Ordinal);
}

internal sealed class GameModelPackageFile
{
    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;
}

internal sealed class InstalledGameModel
{
    public string GameId { get; init; } = string.Empty;

    public int ModelApiVersion { get; init; }

    public int Revision { get; init; }

    public string PackageSha256 { get; init; } = string.Empty;

    public string ModelSha256 { get; init; } = string.Empty;

    public string EventsSha256 { get; init; } = string.Empty;

    public DateTimeOffset InstalledAt { get; init; }

    public string Source { get; init; } = "official";
}

internal sealed class GameModelManifestCache
{
    public DateTimeOffset CheckedAt { get; init; }

    public DateTimeOffset LastAttemptAt { get; init; }

    public string? ETag { get; init; }
}

internal enum GameModelStage
{
    Checking,
    Downloading,
    Verifying,
    Installing,
    Ready,
    Error,
    Unsupported,
}

internal sealed class GameModelStatus
{
    public required string GameId { get; init; }

    public required string Stage { get; init; }

    public int? Revision { get; init; }

    public long? CompletedBytes { get; init; }

    public long? TotalBytes { get; init; }

    public string? Message { get; init; }
}

internal sealed class GameModelStatusMessage
{
    public required IReadOnlyList<GameModelStatus> Models { get; init; }
}
