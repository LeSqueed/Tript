// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.App;

// The IPC wire model (spec/local-ipc.md + Tript.Web/src/ipc/protocol.ts). These are the shapes the
// frontend narrows on, so the field names and casing are a compatibility surface — not a free
// choice. The serialization options are shared so every push behaves identically.
internal static class Wire
{
    internal static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class ContentItem
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string? Title { get; set; }

    public double? StartTime { get; set; }

    public double? EndTime { get; set; }
}

internal sealed class GameInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Detected { get; set; }
}

internal sealed class BookmarkItem
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "Manual";

    public string? Subtype { get; set; }

    public double Time { get; set; }

    public string? Label { get; set; }
}

// The CreateClip command's parameters (protocol.ts CreateClipParameters).
internal sealed class CreateClipParameters
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = "clip";

    public string? Game { get; set; }

    public int? IgdbId { get; set; }

    public string FileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public double StartTime { get; set; }

    public double EndTime { get; set; }

    public List<ClipSegment> Segments { get; set; } = [];

    public string OutputMode { get; set; } = "combine";

    public Dictionary<string, double>? AudioTrackVolumes { get; set; }

    public List<string>? MutedAudioTracks { get; set; }
}

internal sealed class ClipSegment
{
    public double StartTime { get; set; }

    public double EndTime { get; set; }
}

internal sealed class DeleteContentParameters
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;
}

internal sealed class DeleteMultipleContentParameters
{
    public List<DeleteContentParameters> Items { get; set; } = [];
}

internal sealed class RenameContentParameters
{
    public string ContentType { get; set; } = "recording";

    public string FileName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
}

internal sealed class AddBookmarkParameters
{
    public string ContentType { get; set; } = "recording";

    public string FilePath { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public double Time { get; set; }

    public string Type { get; set; } = "Manual";
}

internal sealed class DeleteBookmarkParameters
{
    public string ContentType { get; set; } = "recording";

    public string FilePath { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;
}

internal sealed class UpdateSettingsParameters
{
    public System.Text.Json.JsonElement Settings { get; set; }
}

internal sealed class ToggleFullscreenParameters
{
    public bool Enabled { get; set; }
}

internal sealed class RecoveryConfirmParameters
{
    public string RecoveryId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string? GameOverride { get; set; }
}

internal sealed class NewConnectionParameters
{
    public int ProtocolVersion { get; set; }
}

internal sealed class CancelClipParameters
{
    public string Id { get; set; } = string.Empty;
}
