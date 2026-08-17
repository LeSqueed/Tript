// SPDX-License-Identifier: GPL-2.0-or-later
//
// The local IPC wire contract, from spec/local-ipc.md.
//
// Casing convention (a deliberate fix of the reference's inconsistent casing):
//   - Frontend → backend commands: PascalCase method names, camelCase parameter fields.
//   - Backend → frontend messages: lowercase method names.
// This is a greenfield contract, so the convention is applied everywhere uniformly.

// ---------------------------------------------------------------------------
// Envelope
// ---------------------------------------------------------------------------

/**
 * Frontend → backend envelope. Commands with no arguments send NO `parameters` field at all —
 * not an empty object. Callers must omit it (the serialiser never emits it).
 */
export interface CommandEnvelope {
  method: CommandName;
  parameters?: CommandParameters;
}

/**
 * Backend → frontend envelope. The frontend narrows on `method`.
 */
export interface MessageEnvelope {
  method: MessageName;
  content?: unknown;
}

// ---------------------------------------------------------------------------
// The cause on settings/state pushes
// ---------------------------------------------------------------------------

/**
 * What triggered a settings or state push. The UI distinguishes its own edits echoing back
 * (via `cause` — e.g. a command it just sent) from changes originating elsewhere, so it can
 * avoid fighting the user's typing. Unknown causes are tolerated rather than rejected.
 */
export type ChangeCause = string;

// ---------------------------------------------------------------------------
// Content model (the fields the frontend needs to build content-server URLs)
// ---------------------------------------------------------------------------

/** Content-type discriminator. */
export type ContentType = 'recording' | 'clip' | 'highlight' | 'buffer';

export interface ContentItem {
  contentType: ContentType;
  fileName: string;
  filePath: string;
  title?: string;
  startTime?: number;
  endTime?: number;
  /** Bookmark events inside a recording. Absent (never empty) on clips. */
  bookmarks?: BookmarkItem[];
}

export interface BookmarkItem {
  id: string;
  type: string;
  subtype?: string;
  /** Time offset into the recording, in seconds. */
  time: number;
  label?: string;
}

export interface GameInfo {
  id: string;
  name: string;
  detected: boolean;
  [key: string]: unknown;
}

// ---------------------------------------------------------------------------
// Settings and state
// ---------------------------------------------------------------------------

/** A partial settings object — UpdateSettings accepts a partial; the settings message is full. */
export interface Settings {
  [key: string]: unknown;
}

export interface SettingsMessage {
  settings: Settings;
  cause?: ChangeCause;
}

export interface RecordingState {
  recording: boolean;
  game?: GameInfo | null;
  /** Audio routing status per track, when multi-track recording is active. */
  audioTracks?: { id: string; device: string; muted: boolean; volume: number }[];
  [key: string]: unknown;
}

export interface StateMessage {
  state: RecordingState;
  cause?: ChangeCause;
}

// ---------------------------------------------------------------------------
// Import / update progress
// ---------------------------------------------------------------------------

export interface ImportProgressMessage {
  status: 'importing' | 'done' | 'error';
  content?: ContentItem;
  error?: string;
}

export type UpdateStatus = 'downloading' | 'downloaded' | 'ready' | 'error';

export interface UpdateProgressMessage {
  status: UpdateStatus;
}

export interface ShowModalMessage {
  title: string;
  subtitle?: string;
  description?: string;
  type: 'info' | 'warning' | 'error';
}

export interface StorageWarningMessage {
  warningId: string;
  threshold: number;
  current: number;
}

export interface RecoveryPromptMessage {
  recoveryId: string;
  files: { type: string; typeLabel: string }[];
}

export interface SelectedGameExecutableMessage {
  filePath: string;
}

// ---------------------------------------------------------------------------
// Command parameter shapes
// ---------------------------------------------------------------------------

export interface CreateClipParameters {
  id: string;
  type: ContentType;
  game?: string | null;
  igdbId?: number | null;
  fileName: string;
  filePath: string;
  title: string;
  startTime: number;
  endTime: number;
  segments: ClipSegment[];
  outputMode: 'combine' | 'separate';
  audioTrackVolumes?: Record<string, number>;
  mutedAudioTracks?: string[];
}

export interface ClipSegment {
  startTime: number;
  endTime: number;
}

export interface DeleteContentParameters {
  contentType: ContentType;
  fileName: string;
}

export interface DeleteMultipleContentParameters {
  items: DeleteContentParameters[];
}

export interface RenameContentParameters {
  contentType: ContentType;
  fileName: string;
  title: string;
}

export interface AddBookmarkParameters {
  contentType: ContentType;
  filePath: string;
  id: string;
  time: number;
  type: string;
}

export interface DeleteBookmarkParameters {
  contentType: ContentType;
  filePath: string;
  id: string;
}

export interface ApplyVideoPresetParameters {
  preset: string;
}

export interface ApplyClipPresetParameters {
  preset: string;
}

export interface UpdateSettingsParameters {
  /** A partial settings object. `gameIntegrations` is an observed key. */
  settings: Partial<Settings>;
}

export interface OpenFileLocationParameters {
  filePath: string;
  gameOverride?: string | null;
  recoveryId?: string | null;
}

export interface CopyFileToClipboardParameters {
  filePath: string;
}

export interface OpenInBrowserParameters {
  url: string;
}

export interface StorageWarningConfirmParameters {
  warningId: string;
  confirmed: boolean;
  action?: string | null;
  actionData?: unknown;
}

export interface RecoveryConfirmParameters {
  recoveryId: string;
  action: string;
  gameOverride?: string | null;
}

export interface ToggleFullscreenParameters {
  enabled: boolean;
}

/** The protocol version carried on NewConnection. */
export interface NewConnectionParameters {
  protocolVersion: number;
}

/** Union of every command's parameter shape. */
export type CommandParameters =
  | CreateClipParameters
  | DeleteContentParameters
  | DeleteMultipleContentParameters
  | RenameContentParameters
  | AddBookmarkParameters
  | DeleteBookmarkParameters
  | ApplyVideoPresetParameters
  | ApplyClipPresetParameters
  | UpdateSettingsParameters
  | OpenFileLocationParameters
  | CopyFileToClipboardParameters
  | OpenInBrowserParameters
  | StorageWarningConfirmParameters
  | RecoveryConfirmParameters
  | ToggleFullscreenParameters
  | NewConnectionParameters;

// ---------------------------------------------------------------------------
// Command and message name unions
// ---------------------------------------------------------------------------

/**
 * Frontend → backend commands. PascalCase, exactly as on the wire.
 */
export type CommandName =
  // Recording and lifecycle
  | 'StartRecording'
  | 'StopRecording'
  | 'NewConnection'
  | 'ToggleFullscreen'
  | 'CheckForUpdates'
  | 'ApplyUpdate'
  | 'RefreshStorageStats'
  | 'OpenLogsLocation'
  | 'MigrateContent'
  // Content
  | 'ListContent'
  | 'CreateClip'
  | 'CancelClip'
  | 'DeleteContent'
  | 'DeleteMultipleContent'
  | 'RenameContent'
  | 'ImportFile'
  | 'AddBookmark'
  | 'DeleteBookmark'
  // Settings and presets
  | 'UpdateSettings'
  | 'SetVideoLocation'
  | 'SetCacheLocation'
  | 'SelectGameExecutable'
  | 'ApplyVideoPreset'
  | 'ApplyClipPreset'
  // Shell and OS integration
  | 'OpenFileLocation'
  | 'CopyFileToClipboard'
  | 'OpenInBrowser'
  // Confirmations
  | 'StorageWarningConfirm'
  | 'RecoveryConfirm';

/**
 * Backend → frontend messages, all lowercase.
 *
 * The reference contract split these between lowercase (`settings`, `state`, `importProgress`) and
 * PascalCase (`UpdateProgress`, `ReleaseNotes`, `ShowModal`). Greenfield fix: every backend → frontend
 * method is lowercase. `releaseNotes` and `showReleaseNotes` are both retained (the reference shipped
 * two names for one concern; both are accepted here until the backend settles on one).
 */
export type MessageName =
  | 'settings'
  | 'state'
  | 'content'
  | 'importProgress'
  | 'updateProgress'
  | 'releaseNotes'
  | 'showReleaseNotes'
  | 'showModal'
  | 'storageWarning'
  | 'recoveryPrompt'
  | 'selectedGameExecutable'
  | 'gameList';
