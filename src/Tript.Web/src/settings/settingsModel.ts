// SPDX-License-Identifier: GPL-2.0-or-later
//
// The typed settings model, mirrored from the backend schema (Tript.Settings, page-typed and
// camelCase-serialized). The `settings` message carries this whole object; `UpdateSettings`
// carries a partial. Enums are stored by their C# member names — the frontend must use exactly
// the strings the backend serializes, so the member names below are a compatibility surface, not
// a free choice. The settings object is open-ended on the wire, so the model types carry an
// index signature; a page this build does not fully model still round-trips.

/** Recording mode — Session, Buffer, or Hybrid (both at once). Hybrid is the default. */
export type RecordingMode = 'Session' | 'Buffer' | 'Hybrid';

/**
 * How the encoder is told to spend its bits. The four members are the modes the encoder families
 * actually accept: `Crf` is x264's constant-quality mode and exists nowhere else, `Cqp` is the
 * hardware families' spelling of the same idea, and `Cbr`/`Vbr` are the rate-targeted modes.
 *
 * Which of them a given encoder accepts is a per-family fact, and getting it wrong is not a cosmetic
 * bug: the mode name is written into the encoder's own `rate_control` key, and obs-ffmpeg's VAAPI
 * encoder segfaults on a name it does not know. The backend has the final say — it coerces a mode the
 * resolved encoder cannot use into that family's constant-quality mode — so any value here is a
 * request rather than a promise (see RecordingPage).
 */
export type RateControlMode = 'Crf' | 'Cqp' | 'Cbr' | 'Vbr';

export interface RecordingSettings {
  mode: RecordingMode;
  resolutionWidth: number;
  resolutionHeight: number;
  fps: number;
  /**
   * The encoder id. The settings UI offers the ids the machine supports (the `availableEncoders`
   * field on the settings *message*, not on this page — see SettingsMessageContent); a value from
   * an older config or a per-game override that is not in that list still round-trips.
   */
  encoder: string;
  /**
   * The quality profile, on the backend's own 1..20 scale with higher being better. Only the
   * constant-quality rate-control modes read it; the recorder maps it onto the H.264 quantiser scale
   * the resolved encoder family uses.
   */
  quality: number;
  /**
   * How the encoder spends its bits. Optional on this type because a push from a backend older than
   * this field carries no value at all, and the page must render rather than offer an empty selector.
   */
  rateControl?: RateControlMode;
  /** The target bitrate in kbps, read by the rate-targeted modes (CBR and VBR) only. */
  bitrateKbps?: number;
  /** The VBR ceiling in kbps, or 0 for "derive one from the target". */
  maxBitrateKbps?: number;
  /**
   * The directory recordings are written to, or empty/null for the platform default
   * (Videos/Tript). A path the user types is a local draft, committed on blur like resolution.
   */
  outputDirectory?: string | null;
  [key: string]: unknown;
}

export interface BufferSettings {
  enabled: boolean;
  /** Rolling-buffer length, in seconds. */
  duration: number;
  /** Maximum rolling-buffer size, in bytes. */
  maxSizeBytes: number;
  [key: string]: unknown;
}

/** Audio output handling while recording. */
export type AudioOutputMode = 'Normal' | 'Mute' | 'Disable';

/** Where a captured audio source comes from. */
export type AudioSourceKind = 'Input' | 'Output';

/**
 * One source routed into a track. The source is selected by id because a device can be absent —
 * unplugged, or not present on this machine — while its selection persists. Volume is per-source,
 * never per-track.
 */
export interface AudioSource {
  /** The source's own name (the device or capture-point name). */
  name: string;
  kind: AudioSourceKind;
  /** Human-readable label shown in the routing UI. */
  label?: string;
  /** A device selection: the id/name of the device this source captures, when one is chosen. */
  deviceId?: string;
  deviceName?: string;
  /**
   * The stable routing key used by the UI to prevent a source being routed twice: the device id
   * when this is a device selection, else the built-in source id (mic/system/game). A UI-side
   * convenience; the backend model keys a source by name and device id.
   */
  sourceKey?: string;
  /** Linear volume, 0..1. Default 1.0. */
  volume: number;
  [key: string]: unknown;
}

/**
 * A track is a destination in the output file, not a device — it carries one or more merged
 * sources, each with its own volume (spec/recorder.md).
 */
export interface AudioTrack {
  /** Stable id so a track survives reordering in the UI. */
  id: string;
  name: string;
  sources: AudioSource[];
  [key: string]: unknown;
}

/** An audio device selection from the device list, by id, with the human-readable name. */
export interface AudioDeviceSetting {
  id: string;
  name: string;
  /**
   * The endpoint's data flow: 'Input' for capture endpoints (mics and other capture devices),
   * 'Output' for render endpoints (speakers/headsets). The routing maps Input to
   * wasapi_input_capture and Output to wasapi_output_capture, so the direction decides which
   * capture type a device selection becomes. Absent from an older backend — default 'Input'.
   */
  direction?: AudioSourceKind;
  [key: string]: unknown;
}

export interface AudioSettings {
  outputMode: AudioOutputMode;
  tracks: AudioTrack[];
  devices: AudioDeviceSetting[];
  mic: AudioDeviceSetting | null;
  desktop: AudioDeviceSetting | null;
  [key: string]: unknown;
}

/** Which capture path supplies the picture. */
export type DisplayCaptureMethod = 'Auto' | 'Game' | 'Display';

export interface CaptureSettings {
  method: DisplayCaptureMethod;
  /** The monitor selected for display capture, or null when the method is game capture. */
  display: string | null;
  [key: string]: unknown;
}

/** How the game-capture source behaves. GameOnly selects game capture on the detected process. */
export type GameCaptureMode = 'Auto' | 'GameOnly';

export interface GameRecordingModeOverride {
  mode: RecordingMode;
  [key: string]: unknown;
}

export interface GameQualityOverride {
  resolutionWidth?: number | null;
  resolutionHeight?: number | null;
  fps?: number | null;
  encoder?: string | null;
  quality?: number | null;
  [key: string]: unknown;
}

export interface GameIntegrationSettings {
  enabled: boolean;
  [key: string]: unknown;
}

export interface GameSetting {
  id: string;
  name: string;
  iconId?: string | null;
  recordingModeOverride?: GameRecordingModeOverride | null;
  qualityOverride?: GameQualityOverride | null;
  integrations: GameIntegrationSettings;
  [key: string]: unknown;
}

export interface GameSettings {
  captureMode: GameCaptureMode;
  /** How long game capture waits for the game's window before falling back, in seconds. */
  gameCaptureTimeout: number;
  gameList: GameSetting[];
  [key: string]: unknown;
}

/** The full settings object, page-typed and open-ended, mirroring the backend schema. */
export interface SettingsModel {
  recording: RecordingSettings;
  buffer: BufferSettings;
  audio: AudioSettings;
  capture: CaptureSettings;
  game: GameSettings;
  [key: string]: unknown;
}

export interface SettingsMessageContent {
  settings: SettingsModel;
  cause?: string;
  /**
   * The H.264 encoder ids this machine's runtime actually registered, settled on the backend
   * (`ObsRecorderSession.EnumerateUsableEncoderIds`). It rides the settings push as a **sibling**
   * of `settings` rather than as a field of the recording page, because it is a property of the
   * running machine and not a persisted setting — the backend's recording page has no such
   * property, so a nested copy would be round-tripped into its extension data and written to disk.
   *
   * Absent from an older backend, and explicitly null from a host that cannot probe the encoder
   * registry (the fake-recorder host never loads libobs, so asking would crash rather than answer).
   * Both mean "unknown" and the encoder selector falls back.
   */
  availableEncoders?: string[] | null;
  /**
   * The primary display's pixel size, detected once by the host at startup
   * (`Tript.App/PrimaryDisplay.cs`). Like `availableEncoders` it rides the push as a **sibling** of
   * `settings`: it describes this machine rather than the configuration, and nesting it under the
   * recording page would round-trip it into that page's extension data and write it to the settings
   * file.
   *
   * Absent from an older backend and explicitly null when detection failed (no display server, or a
   * platform we could not read a monitor from). Both mean "unknown", and the resolution selector
   * then offers its preset list alone.
   */
  displayResolution?: DisplayResolution | null;
}

/** The pixel size of a display, as the settings push reports it. */
export interface DisplayResolution {
  width: number;
  height: number;
}
