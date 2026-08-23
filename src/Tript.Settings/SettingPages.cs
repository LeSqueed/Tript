// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

// Each logical page is a typed object under Settings. The UI loads and saves one page at a time
// (see SettingsStore), and the alpha recorder consumes the resolved values via
// ResolvedRecorderSettings rather than these raw page objects.

// The recording page: every mode writes a continuous session. The combined mode additionally keeps
// an explicitly requested replay buffer alive for live highlight saves.
public sealed class RecordingSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    // New users get both outputs. Existing persisted Session values remain Session and are never
    // upgraded implicitly, so enabling replay is an explicit migration choice for them.
    public RecordingMode Mode { get; set; } = RecordingMode.SessionWithReplayBuffer;

    public int ResolutionWidth { get; set; } = 1920;

    public int ResolutionHeight { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    public string Encoder { get; set; } = "x264";

    // The quality profile applied when a game has no override of its own. The app's own 1..20
    // scale, higher being better; the recorder maps it onto the H.264 quantiser scale the resolved
    // encoder family reads (ObsRecorderSession.MapQualityToQuantiser).
    public int Quality { get; set; } = 10;

    // How the encoder is told to spend its bits. Cqp is the default because it means "constant
    // quality" on every machine: an encoder family that does not accept CQP — x264 — has the choice
    // coerced into its own constant-quality mode (CRF) by the recorder, so the default records the
    // user's intent rather than one family's spelling of it.
    public RateControlMode RateControl { get; set; } = RateControlMode.Cqp;

    // The target bitrate for the rate-targeted modes (CBR and VBR), in kbps. Ignored by the
    // constant-quality modes. 15 Mbps is the rule-of-thumb "looks right" figure for 1080p60 H.264
    // local recording — well above a streaming bitrate, because nothing here is being uploaded.
    public int BitrateKbps { get; set; } = 15_000;

    // The VBR ceiling, in kbps, or 0 for "derive it from the target". Only the families that
    // document a ceiling key receive it (NVENC and QSV have max_bitrate; AMF has none at all, and
    // x264's ceiling is its VBV pair) — see ObsRecorderSession for which key each family reads.
    public int MaxBitrateKbps { get; set; }

    // Whether to record in HDR when the captured display is in HDR mode. On by default because the
    // alternative is worse than a preference: an HDR game hands win-capture an FP16 scRGB swapchain,
    // and composited into an SDR canvas that is a black or washed-out recording. Turning this off
    // does not go back to that — it makes the capture sources tonemap to SDR instead, which is the
    // right answer for anyone whose player cannot handle a PQ file.
    //
    // Ignored where nothing can act on it: HDR needs a display in HDR mode, an HEVC or AV1 encoder,
    // and (today) Windows. Any of those missing records SDR with tonemapping.
    public bool EnableHdr { get; set; } = true;

    // The directory recordings are written to, or empty for the platform default (Videos/Tript).
    // The host resolves the effective path; the recorder never sees this field.
    public string? OutputDirectory { get; set; }

    // Whether detected positive events should be turned into highlights automatically. In combined
    // mode the replay buffer still runs when this is false, ready for future manual hotkeys.
    public bool AutomaticClipsEnabled { get; set; }

    // How long a deleted recording stays in the trash before it is purged for good. Zero or less
    // disables the automatic purge, so entries stay until they are emptied by hand.
    //
    // A week, not a day. The trash only ever holds what the user chose to delete, so the cost of
    // keeping it is bounded by their own actions, while the cost of purging too early is a recording
    // that cannot be got back. Twenty-four hours does not survive "I deleted it Friday and noticed
    // on Monday", which is the case a trash exists for.
    public int TrashRetentionHours { get; set; } = 168;
}

// The buffer page, its own first-class settings surface even though the buffer itself is
// deferred in alpha (design decision 2026-08-15). Both bounds exist because memory is the real
// constraint: duration alone at high resolution and bitrate can be unbounded.
public sealed class BufferSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool Enabled { get; set; }

    // Configurable, default 30 seconds.
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(30);

    // Configurable, independent of duration.
    public long MaxSizeBytes { get; set; } = 4L * 1024 * 1024 * 1024;
}

// The audio page drives the multi-track model: the user chooses how many tracks a recording has and
// routes sources (mics, system output, game audio) into them, each with its own volume. A track is
// a destination, not a device; one track may carry several merged sources.
public sealed class AudioSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public AudioOutputMode OutputMode { get; set; } = AudioOutputMode.Normal;

    public List<AudioTrack> Tracks { get; set; } = [];

    public List<AudioDeviceSetting> Devices { get; set; } = [];

    public AudioDeviceSetting? Mic { get; set; }

    public AudioDeviceSetting? Desktop { get; set; }
}

// An audio device selection from the device list, by id, with the human-readable name recorded
// for the metadata and the settings UI. Direction is the endpoint's data flow: Input for capture
// endpoints (mics and other capture devices), Output for render endpoints (speakers), so the
// routing picks the matching capture source type (wasapi_input_capture vs wasapi_output_capture).
public sealed class AudioDeviceSetting
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AudioSourceKind Direction { get; set; }
}

// The capture page: which capture path supplies the picture, and the optional display selection
// when the method is a display/monitor capture.
public sealed class CaptureSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public DisplayCaptureMethod Method { get; set; } = DisplayCaptureMethod.Auto;

    // The monitor selected for display capture, or null for the primary monitor. The stable id the
    // capture plugin matches on, never an index: monitors renumber when one is unplugged.
    public string? Display { get; set; }

    // The human name last seen for Display, so a monitor that is no longer attached can be named in
    // a warning. Never matched on — the id is the identity.
    public string? DisplayLabel { get; set; }
}

// The game page: the capture-mode behaviour (GameOnly is our own prior work and part of first
// light) and the list of known games with their per-game overrides.
public sealed class GameSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    // The soft timeout: how long game capture waits for the game's window before falling back.
    // What the timeout governs is part of the recorder's contract; the settings model records
    // the value.
    public TimeSpan GameCaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // The known games. A fresh install ships with Overwatch so the auto-start detection watches
    // a game on first launch; a user who edits the list keeps exactly what they saved, because
    // this default only applies when a settings file is absent or has no gameList key.
    public List<GameSetting> GameList { get; set; } = [new() { Id = "Overwatch", Name = "Overwatch" }];
}

// The general page owns desktop-shell preferences rather than recorder configuration. The headless
// host persists and exposes these values too, while the desktop shell is responsible for applying
// the platform-specific effects.
public sealed class GeneralSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool StartWithWindows { get; set; }

    public StartupVisibility StartupVisibility { get; set; } = StartupVisibility.Window;

    public MinimizeBehavior MinimizeBehavior { get; set; } = MinimizeBehavior.Taskbar;

    public CloseBehavior CloseBehavior { get; set; } = CloseBehavior.Exit;

    public NotificationSettings Notifications { get; set; } = new();
}

public sealed class NotificationSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public bool RecordingStarted { get; set; } = true;

    public bool RecordingStopped { get; set; } = true;

    public bool Errors { get; set; } = true;

    public bool Recovery { get; set; } = true;
}
