// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

// The seam the dependency map flagged: the recorder receives already-resolved values, never the
// settings schema. This resolver computes the effective value — global setting plus any per-game
// override — and hands consumers a flat, resolved config. The recorder consumes
// ResolvedRecorderSettings and knows nothing about Settings, overrides, or the merge.
public sealed class SettingsResolver
{
    public static ResolvedRecorderSettings Resolve(Settings settings, string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GameSetting? game = null;
        if (!string.IsNullOrEmpty(gameId))
            game = settings.Game.GameList.FirstOrDefault(g => g.Id == gameId);

        return new ResolvedRecorderSettings
        {
            Mode = ResolveMode(settings.Recording.Mode, game?.RecordingModeOverride?.Mode),

            ResolutionWidth = game?.QualityOverride?.ResolutionWidth ?? settings.Recording.ResolutionWidth,
            ResolutionHeight = game?.QualityOverride?.ResolutionHeight ?? settings.Recording.ResolutionHeight,
            Fps = game?.QualityOverride?.Fps ?? settings.Recording.Fps,
            Encoder = game?.QualityOverride?.Encoder ?? settings.Recording.Encoder,
            Quality = game?.QualityOverride?.Quality ?? settings.Recording.Quality,

            BufferEnabled = settings.Buffer.Enabled,
            BufferDuration = settings.Buffer.Duration,
            BufferMaxSizeBytes = settings.Buffer.MaxSizeBytes,

            CaptureMethod = settings.Capture.Method,
            Display = settings.Capture.Display,

            AudioTracks = [.. settings.Audio.Tracks]
        };
    }

    // A per-game recording-mode override participates only when it is set; otherwise the global
    // mode is the effective value.
    private static RecordingMode ResolveMode(RecordingMode global, RecordingMode? gameOverride)
        => gameOverride ?? global;
}

// The flat, resolved configuration the recorder consumes. Mutable for the alpha (a state machine
// that needs to adjust a resolved value as it runs), with a clone for callers that hand it to
// something asynchronous.
public sealed class ResolvedRecorderSettings
{
    public RecordingMode Mode { get; set; }

    public int ResolutionWidth { get; set; }

    public int ResolutionHeight { get; set; }

    public int Fps { get; set; }

    public string Encoder { get; set; } = string.Empty;

    public int Quality { get; set; }

    public bool BufferEnabled { get; set; }

    public TimeSpan BufferDuration { get; set; }

    public long BufferMaxSizeBytes { get; set; }

    public DisplayCaptureMethod CaptureMethod { get; set; }

    public string? Display { get; set; }

    public List<AudioTrack> AudioTracks { get; set; } = [];

    public ResolvedRecorderSettings Clone() => new()
    {
        Mode = Mode,
        ResolutionWidth = ResolutionWidth,
        ResolutionHeight = ResolutionHeight,
        Fps = Fps,
        Encoder = Encoder,
        Quality = Quality,
        BufferEnabled = BufferEnabled,
        BufferDuration = BufferDuration,
        BufferMaxSizeBytes = BufferMaxSizeBytes,
        CaptureMethod = CaptureMethod,
        Display = Display,
        AudioTracks = [.. AudioTracks]
    };
}
