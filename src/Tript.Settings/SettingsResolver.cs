// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

// The seam the dependency map flagged: the recorder receives already-resolved values, never the
// settings schema. This resolver computes the effective value — global setting plus any per-game
// override — and hands consumers a flat, resolved config.
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

            // Rate control has no per-game override: the per-game quality override type is the one
            // the settings schema defines (resolution, fps, encoder, quality) and growing it is a
            // separate decision. The global choice is therefore the effective one for every game.
            RateControl = settings.Recording.RateControl,
            BitrateKbps = settings.Recording.BitrateKbps,
            MaxBitrateKbps = settings.Recording.MaxBitrateKbps,
            EnableHdr = settings.Recording.EnableHdr,

            BufferEnabled = settings.Buffer.Enabled,
            BufferDuration = settings.Buffer.Duration,
            BufferMaxSizeBytes = settings.Buffer.MaxSizeBytes,

            CaptureMethod = settings.Capture.Method,
            Display = settings.Capture.Display,

            // The capture policy is global: which layers the scene has, and how long a game-only
            // capture waits for its hook, are not per-game overrides.
            GameCaptureTimeout = settings.Game.GameCaptureTimeout,

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

    // Where the recording is written. The settings resolver owns storage decisions; the recorder
    // consumes the resolved path.
    public string OutputPath { get; set; } = string.Empty;

    public int ResolutionWidth { get; set; }

    public int ResolutionHeight { get; set; }

    public int Fps { get; set; }

    public string Encoder { get; set; } = string.Empty;

    public int Quality { get; set; }

    // How the encoder spends its bits, and the kbps figures the rate-targeted modes use. The
    // recorder validates the mode against the encoder family it actually resolved and coerces an
    // unsupported one; a resolved value is a request, not a promise (ObsRecorderSession).
    public RateControlMode RateControl { get; set; }

    public int BitrateKbps { get; set; }

    public int MaxBitrateKbps { get; set; }

    // A request, not a promise: HDR is taken only when the captured display is actually in HDR mode
    // and a registered encoder can encode it (HdrPlanner.Decide). False means the capture sources
    // tonemap an HDR game down instead.
    public bool EnableHdr { get; set; } = true;

    public bool BufferEnabled { get; set; }

    public TimeSpan BufferDuration { get; set; }

    public long BufferMaxSizeBytes { get; set; }

    public DisplayCaptureMethod CaptureMethod { get; set; }

    public string? Display { get; set; }

    // How long game capture is given to attach before the Game method gives up. Only the Game
    // method acts on it: Auto has a display layer to show meanwhile.
    public TimeSpan GameCaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public List<AudioTrack> AudioTracks { get; set; } = [];

    public ResolvedRecorderSettings Clone() => new()
    {
        Mode = Mode,
        OutputPath = OutputPath,
        ResolutionWidth = ResolutionWidth,
        ResolutionHeight = ResolutionHeight,
        Fps = Fps,
        Encoder = Encoder,
        Quality = Quality,
        RateControl = RateControl,
        BitrateKbps = BitrateKbps,
        MaxBitrateKbps = MaxBitrateKbps,
        EnableHdr = EnableHdr,
        BufferEnabled = BufferEnabled,
        BufferDuration = BufferDuration,
        BufferMaxSizeBytes = BufferMaxSizeBytes,
        CaptureMethod = CaptureMethod,
        Display = Display,
        GameCaptureTimeout = GameCaptureTimeout,
        AudioTracks = [.. AudioTracks]
    };
}
