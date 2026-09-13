// SPDX-License-Identifier: GPL-2.0-or-later

export type RecordingMode = 'Session' | 'SessionWithReplayBuffer' | 'ReplayBufferOnly';

export type RateControlMode = 'Crf' | 'Cqp' | 'Cbr' | 'Vbr';

export interface RecordingSettings {
  mode: RecordingMode;
  resolutionWidth: number;
  resolutionHeight: number;
  fps: number;
  encoder: string;
  quality: number;
  rateControl?: RateControlMode;
  bitrateKbps?: number;
  maxBitrateKbps?: number;
  enableHdr?: boolean;
  outputDirectory?: string | null;
  automaticClipsEnabled?: boolean;
  automaticClipBeforeSeconds?: number;
  automaticClipAfterSeconds?: number;
  deleteLinkedHighlightsByDefault?: boolean;
  trashRetentionHours?: number;
  [key: string]: unknown;
}

export interface BufferSettings {
  enabled?: boolean;
  duration: number;
  maxSizeBytes: number;
  [key: string]: unknown;
}

export type AudioOutputMode = 'Normal' | 'Mute' | 'Disable';

export type AudioSourceKind = 'Input' | 'Output';

export interface AudioSource {
  name: string;
  kind: AudioSourceKind;
  label?: string;
  deviceId?: string;
  deviceName?: string;
  sourceKey?: string;
  volume: number;
  [key: string]: unknown;
}

export interface AudioTrack {
  id: string;
  name: string;
  sources: AudioSource[];
  [key: string]: unknown;
}

export interface AudioDeviceSetting {
  id: string;
  name: string;
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

export type DisplayCaptureMethod = 'Auto' | 'Game' | 'Display';

export interface CaptureSettings {
  method: DisplayCaptureMethod;
  display: string | null;
  displayLabel?: string | null;
  [key: string]: unknown;
}

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

export interface GameAutomaticClipOverride {
  beforeSeconds?: number | null;
  afterSeconds?: number | null;
}

export interface GameSetting {
  id: string;
  name: string;
  executablePath?: string | null;
  executable?: string | null;
  iconId?: string | null;
  autoRecordOverride?: boolean | null;
  recordingModeOverride?: GameRecordingModeOverride | null;
  captureMethodOverride?: GameCaptureMethodOverride | null;
  qualityOverride?: GameQualityOverride | null;
  automaticClipOverride?: GameAutomaticClipOverride | null;
  [key: string]: unknown;
}

export interface GameSettings {
  gameCaptureTimeout: number;
  gameList: GameSetting[];
  autoRecordDetectedGames?: boolean;
  ignoredApplications?: string[];
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

export type HotkeyAction = 'ToggleRecording' | 'ManualBookmark' | 'QuickClip';

export interface HotkeyBinding {
  modifiers: string[];
  key: string | null;
  [key: string]: unknown;
}

export interface HotkeySettings {
  enabled: boolean;
  toggleRecording: HotkeyBinding;
  manualBookmark: HotkeyBinding;
  quickClip: HotkeyBinding;
  quickClipSeconds: number;
  [key: string]: unknown;
}

export interface SettingsModel {
  recording: RecordingSettings;
  buffer: BufferSettings;
  audio: AudioSettings;
  capture: CaptureSettings;
  game: GameSettings;
  general: GeneralSettings;
  hotkeys: HotkeySettings;
  [key: string]: unknown;
}

export interface SettingsMessageContent {
  settings: SettingsModel;
  cause?: string;
  availableEncoders?: string[] | null;
  displayResolution?: DisplayResolution | null;
  availableDisplays?: DisplayInfo[] | null;
  displayFallbackWarning?: DisplayFallbackWarning | null;
}

export interface DisplayResolution {
  width: number;
  height: number;
}

export interface DisplayInfo {
  id: string;
  name: string;
  width: number;
  height: number;
  primary: boolean;
}

export interface DisplayFallbackWarning {
  requestedId: string;
  requestedLabel: string | null;
  usingId: string | null;
  usingLabel: string | null;
}
