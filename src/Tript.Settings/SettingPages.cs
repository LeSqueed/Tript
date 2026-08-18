// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tript.Settings;

// Each logical page is a typed object under Settings. The UI loads and saves one page at a time
// (see SettingsStore), and the alpha recorder consumes the resolved values via
// ResolvedRecorderSettings rather than these raw page objects. Each page carries JsonExtensionData
// so a per-page save cannot drop a field from a page this build does not fully model.

// The recording page: the session recording itself. Alpha records a single-output session; the
// mode is first-class from the start because the buffer/hybrid two-output shape slots in without
// a refactor (spec/recorder.md, design decision 2026-08-15).
public sealed class RecordingSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public RecordingMode Mode { get; set; } = RecordingMode.Hybrid;

    public int ResolutionWidth { get; set; } = 1920;

    public int ResolutionHeight { get; set; } = 1080;

    public int Fps { get; set; } = 60;

    public string Encoder { get; set; } = "x264";

    // The quality profile applied when a game has no override of its own. The app's own 1..20 scale,
    // higher being better; the recorder maps it onto the H.264 quantiser scale the resolved encoder
    // family reads (ObsRecorderSession.MapQualityToQuantiser). Only the constant-quality rate-control
    // modes consume it.
    public int Quality { get; set; } = 10;

    // How the encoder is told to spend its bits. Cqp is the default because it means "constant
    // quality" on every machine: an encoder family that does not accept CQP — x264 — has the choice
    // coerced into its own constant-quality mode (CRF) by the recorder, so the default records the
    // user's intent rather than one family's spelling of it. The coercion is what keeps a settings
    // file written on one machine from crashing on another, and it lives with the recorder because
    // only the recorder knows which encoder id the runtime resolved.
    public RateControlMode RateControl { get; set; } = RateControlMode.Cqp;

    // The target bitrate for the rate-targeted modes (CBR and VBR), in kbps. Ignored by the
    // constant-quality modes. 15 Mbps is the rule-of-thumb "looks right" figure for 1080p60 H.264
    // local recording — well above a streaming bitrate, because nothing here is being uploaded.
    public int BitrateKbps { get; set; } = 15_000;

    // The VBR ceiling, in kbps, or 0 for "derive it from the target". Only the families that
    // document a ceiling key receive it (NVENC and QSV have max_bitrate; AMF has none at all, and
    // x264's ceiling is its VBV pair) — see ObsRecorderSession for which key each family reads.
    public int MaxBitrateKbps { get; set; }

    // The directory recordings are written to, or empty for the platform default (Videos/Tript).
    // The host resolves the effective path; the recorder never sees this field.
    public string? OutputDirectory { get; set; }
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

// The audio page drives the multi-track model: the user chooses how many tracks a recording has
// and routes sources (mics, system output, game audio) into them, each with its own volume.
// A track is a destination, not a device; one track may carry several merged sources
// (spec/recorder.md). The source is selected by id because a device can be absent — unplugged,
// or not present on this machine — while its selection persists.
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

    // The monitor selected for display capture, or null when the method is game capture.
    public string? Display { get; set; }
}

// The game page: the capture-mode behaviour (GameOnly is our own prior work and part of first
// light) and the list of known games with their per-game overrides.
public sealed class GameSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> UnknownProperties { get; set; } = new();

    public GameCaptureMode CaptureMode { get; set; }

    // The soft timeout: how long game capture waits for the game's window before falling back.
    // What the timeout governs is part of the recorder's contract; the settings model records
    // the value.
    public TimeSpan GameCaptureTimeout { get; set; } = TimeSpan.FromSeconds(10);

    // The known games. A fresh install ships with Overwatch so the auto-start detection watches
    // a game on first launch; a user who edits the list keeps exactly what they saved, because
    // this default only applies when a settings file is absent or has no gameList key.
    public List<GameSetting> GameList { get; set; } = [new() { Id = "Overwatch", Name = "Overwatch" }];
}
