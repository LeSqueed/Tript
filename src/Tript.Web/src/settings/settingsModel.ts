// SPDX-License-Identifier: GPL-2.0-or-later
//
// The typed settings model, mirrored from the backend schema (Tript.Settings, page-typed and
// camelCase-serialized). The `settings` message carries this whole object; `UpdateSettings` carries
// a partial.

/** Every mode records a session; the combined mode additionally runs the replay buffer. */
export type RecordingMode = 'Session' | 'SessionWithReplayBuffer';

/**
 * How the encoder is told to spend its bits. The four members are the modes the encoder families
 * actually accept: `Crf` is x264's constant-quality mode and exists nowhere else, `Cqp` is the
 * hardware families' spelling of the same idea, and `Cbr`/`Vbr` are the rate-targeted modes.
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
   * Whether to record HDR when the captured display is in HDR mode. Optional for the same reason as
   * rateControl: a push from a backend without the field must still render. Undefined reads as on,
   * matching the backend default.
   */
  enableHdr?: boolean;
  /**
   * The directory recordings are written to, or empty/null for the platform default
   * (Videos/Tript). A path the user types is a local draft, committed on blur like resolution.
   */
  outputDirectory?: string | null;
  /** Create detected highlights automatically when a recording stops. Off by default. */
  automaticClipsEnabled?: boolean;
  /**
   * How many seconds before a detected highlight's start the clip begins. Optional as with
   * `rateControl`: a push from a backend without the field must still render.
   */
  automaticClipBeforeSeconds?: number;
  /**
   * How many seconds after a detected highlight's end the clip continues. Optional for the same
   * reason as `automaticClipBeforeSeconds`.
   */
  automaticClipAfterSeconds?: number;
  /** Initial linked-highlight choice in session deletion confirmations. Undefined reads as false. */
  deleteLinkedHighlightsByDefault?: boolean;
  /**
   * How long deleted items stay in the trash before they are purged automatically, in hours.
   * Zero or less keeps them until the trash is emptied by hand. Optional on this type as with
   * `rateControl`: a push from a backend without the field must still render.
   */
  trashRetentionHours?: number;
  [key: string]: unknown;
}

export interface BufferSettings {
  /** Legacy field retained in persisted settings; recording mode is the activation authority. */
  enabled?: boolean;
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
 * unplugged, or not present on this machine — while its selection persists.
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
 * sources, each with its own volume.
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
  /**
   * The preferred monitor's stable id, or null meaning "the primary monitor". Never rewritten when
   * that monitor is absent: the recorder falls back for the session and reports it, so an unplugged
   * monitor resumes being the choice when it comes back.
   */
  display: string | null;
  /**
   * The human name last seen for `display`. Exists ONLY so a warning can name a monitor that is no
   * longer attached — never used for matching.
   */
  displayLabel?: string | null;
  [key: string]: unknown;
}

/** How the game-capture source behaves. GameOnly selects game capture on the detected process. */

/** Per-game capture method; absent means "inherit the global Capture setting". */
export interface GameCaptureMethodOverride {
  method: DisplayCaptureMethod;
}

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

/**
 * Per-game automatic-clip padding. Null or undefined for a side inherits the global
 * `automaticClipBeforeSeconds`/`automaticClipAfterSeconds` value for that side.
 */
export interface GameAutomaticClipOverride {
  beforeSeconds?: number | null;
  afterSeconds?: number | null;
}

export interface GameIntegrationSettings {
  enabled: boolean;
  [key: string]: unknown;
}

export interface GameSetting {
  id: string;
  name: string;
  /** The exact executable path for a user-defined game. */
  executablePath?: string | null;
  /**
   * The process name the recorder watches for and attaches game capture to. Absent or null means
   * "the same as `name`", which is what every settings file written before this field existed says.
   */
  executable?: string | null;
  iconId?: string | null;
  recordingModeOverride?: GameRecordingModeOverride | null;
  captureMethodOverride?: GameCaptureMethodOverride | null;
  qualityOverride?: GameQualityOverride | null;
  /**
   * Per-game automatic-clip padding. Absent means this game uses the global recording-page values;
   * within the override, a null/undefined side likewise inherits the global value for that side.
   */
  automaticClipOverride?: GameAutomaticClipOverride | null;
  integrations: GameIntegrationSettings;
  [key: string]: unknown;
}

export interface GameSettings {
  /** How long game capture waits for the game's window before falling back, in seconds. */
  gameCaptureTimeout: number;
  gameList: GameSetting[];
  [key: string]: unknown;
}

export type StartupVisibility = 'Window' | 'Minimized' | 'Tray';
export type MinimizeBehavior = 'Taskbar' | 'Tray';
export type CloseBehavior = 'Exit' | 'HideToTray';

export interface NotificationSettings {
  enabled: boolean;
  recordingStarted: boolean;
  recordingStopped: boolean;
  errors: boolean;
  recovery: boolean;
  [key: string]: unknown;
}

export interface GeneralSettings {
  startWithWindows: boolean;
  startupVisibility: StartupVisibility;
  minimizeBehavior: MinimizeBehavior;
  closeBehavior: CloseBehavior;
  convertHdrClipsToSdr?: boolean;
  notifications: NotificationSettings;
  [key: string]: unknown;
}

/** The full settings object, page-typed and open-ended, mirroring the backend schema. */
export interface SettingsModel {
  recording: RecordingSettings;
  buffer: BufferSettings;
  audio: AudioSettings;
  capture: CaptureSettings;
  game: GameSettings;
  general: GeneralSettings;
  [key: string]: unknown;
}

export interface SettingsMessageContent {
  settings: SettingsModel;
  cause?: string;
  /**
   * The H.264 encoder ids this machine's runtime actually registered, settled on the backend
   * from the backend encoder policy. It rides the settings push as a **sibling**
   * of `settings` rather than as a field of the recording page, because it is a property of the
   * running machine and not a persisted setting — the backend's recording page has no such
   * property, so a nested copy would be round-tripped into its extension data and written to disk.
   */
  availableEncoders?: string[] | null;
  /**
   * The primary display's pixel size, detected once by the host at startup
   * (`Tript.App/PrimaryDisplay.cs`). Like `availableEncoders` it rides the push as a **sibling** of
   * `settings`: it describes this machine rather than the configuration, and nesting it under the
   * recording page would round-trip it into that page's extension data and write it to the settings
   * file.
   */
  displayResolution?: DisplayResolution | null;
  /**
   * The monitors this machine has right now, for the capture page's picker. A **sibling** of
   * `settings` for the same reason the two above are: it describes the machine, not the
   * configuration.
   */
  availableDisplays?: DisplayInfo[] | null;
  /**
   * Set when `capture.display` names a monitor that is not attached right now, so the recorder fell
   * back. Also a sibling of `settings`, and likewise never written back.
   */
  displayFallbackWarning?: DisplayFallbackWarning | null;
}

/** The pixel size of a display, as the settings push reports it. */
export interface DisplayResolution {
  width: number;
  height: number;
}

/** One monitor, as the settings push enumerates it. `id` is what `capture.display` stores. */
export interface DisplayInfo {
  id: string;
  /** Human label, e.g. "DP-1" or "\\.\DISPLAY1". */
  name: string;
  width: number;
  height: number;
  primary: boolean;
}

/** The fallback the recorder made because the preferred monitor is not attached. */
export interface DisplayFallbackWarning {
  requestedId: string;
  /** From `capture.displayLabel`; null when nothing ever recorded a name for that id. */
  requestedLabel: string | null;
  /** The monitor actually being used, when the host can name it. */
  usingId: string | null;
  usingLabel: string | null;
}
