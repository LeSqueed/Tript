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

export interface RecordingSettings {
  mode: RecordingMode;
  resolutionWidth: number;
  resolutionHeight: number;
  fps: number;
  encoder: string;
  quality: number;
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
}
