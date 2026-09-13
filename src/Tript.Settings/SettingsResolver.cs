// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

namespace Tript.Settings;

public sealed class SettingsResolver
{
    public static ResolvedRecorderSettings Resolve(Settings settings, string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GameSetting? game = null;
        if (!string.IsNullOrEmpty(gameId))
            game = settings.Game.GameList.FirstOrDefault(g =>
                string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));

        var mode = ResolveMode(settings.Recording.Mode, game?.RecordingModeOverride?.Mode);

        return new ResolvedRecorderSettings
        {
            Mode = mode,

            ResolutionWidth = game?.QualityOverride?.ResolutionWidth ?? settings.Recording.ResolutionWidth,
            ResolutionHeight = game?.QualityOverride?.ResolutionHeight ?? settings.Recording.ResolutionHeight,
            Fps = game?.QualityOverride?.Fps ?? settings.Recording.Fps,
            Encoder = game?.QualityOverride?.Encoder ?? settings.Recording.Encoder,
            Quality = game?.QualityOverride?.Quality ?? settings.Recording.Quality,

            RateControl = settings.Recording.RateControl,
            BitrateKbps = settings.Recording.BitrateKbps,
            MaxBitrateKbps = settings.Recording.MaxBitrateKbps,
            EnableHdr = settings.Recording.EnableHdr,

            BufferEnabled = mode is RecordingMode.SessionWithReplayBuffer or RecordingMode.ReplayBufferOnly,
            BufferDuration = settings.Buffer.Duration,
            BufferMaxSizeBytes = settings.Buffer.MaxSizeBytes,

            CaptureMethod = game?.CaptureMethodOverride?.Method ?? settings.Capture.Method,
            Display = settings.Capture.Display,

            GameCaptureTimeout = settings.Game.GameCaptureTimeout,

            AudioTracks = [.. settings.Audio.Tracks]
        };
    }

    private static RecordingMode ResolveMode(RecordingMode global, RecordingMode? gameOverride)
        => gameOverride ?? global;

    public static (TimeSpan Before, TimeSpan After) ResolveAutomaticClipWindow(Settings settings, string? gameId)
    {
        ArgumentNullException.ThrowIfNull(settings);

        GameSetting? game = null;
        if (!string.IsNullOrEmpty(gameId))
            game = settings.Game.GameList.FirstOrDefault(g =>
                string.Equals(g.Id, gameId, StringComparison.OrdinalIgnoreCase));

        var beforeSeconds = game?.AutomaticClipOverride?.BeforeSeconds
            ?? settings.Recording.AutomaticClipBeforeSeconds;
        var afterSeconds = game?.AutomaticClipOverride?.AfterSeconds
            ?? settings.Recording.AutomaticClipAfterSeconds;

        beforeSeconds = Math.Max(0, beforeSeconds);
        afterSeconds = Math.Max(0, afterSeconds);
        afterSeconds = Math.Max(beforeSeconds, afterSeconds);

        return (TimeSpan.FromSeconds(beforeSeconds), TimeSpan.FromSeconds(afterSeconds));
    }

    // gameId is unused today — reserved for a future per-game hotkey override layer, which would
    // consult it the same way every other Resolve* method here consults game?.XyzOverride ?? global.
    public static IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> ResolveEffectiveHotkeys(
        Settings settings, string? gameId = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var hotkeys = settings.Hotkeys;
        if (!hotkeys.Enabled)
        {
            return new Dictionary<HotkeyAction, HotkeyBinding?>
            {
                [HotkeyAction.ToggleRecording] = null,
                [HotkeyAction.ManualBookmark] = null,
                [HotkeyAction.QuickClip] = null,
            };
        }

        return new Dictionary<HotkeyAction, HotkeyBinding?>
        {
            [HotkeyAction.ToggleRecording] = hotkeys.ToggleRecording,
            [HotkeyAction.ManualBookmark] = hotkeys.ManualBookmark,
            [HotkeyAction.QuickClip] = hotkeys.QuickClip,
        };
    }
}

public sealed class ResolvedRecorderSettings
{
    public RecordingMode Mode { get; set; }

    public string OutputPath { get; set; } = string.Empty;

    public int ResolutionWidth { get; set; }

    public int ResolutionHeight { get; set; }

    public int Fps { get; set; }

    public string Encoder { get; set; } = string.Empty;

    public int Quality { get; set; }

    public RateControlMode RateControl { get; set; }

    public int BitrateKbps { get; set; }

    public int MaxBitrateKbps { get; set; }

    public bool EnableHdr { get; set; } = true;

    public bool BufferEnabled { get; set; }

    public TimeSpan BufferDuration { get; set; }

    public long BufferMaxSizeBytes { get; set; }

    public DisplayCaptureMethod CaptureMethod { get; set; }

    public string? Display { get; set; }

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
