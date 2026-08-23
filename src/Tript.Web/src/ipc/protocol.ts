// SPDX-License-Identifier: GPL-2.0-or-later
//
// The local IPC wire contract.
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

/**
 * One item in the backend's content list. EVERY field but the three the content server cannot serve
 * without (`contentType`, `fileName`, `filePath`) is optional, and that is not defensive decoration
 * — it is the observed shape of the wire.
 */
export interface ContentItem {
  contentType: ContentType;
  fileName: string;
  filePath: string;
  title?: string;
  favorite?: boolean;
  /**
   * When the recording started, in Unix epoch SECONDS (not milliseconds). Absent when the item has
   * no metadata record.
   */
  startTime?: number;
  endTime?: number;
  /**
   * The game the item was recorded from, as the backend detected it. Nullable *and* optional: null
   * from a backend that looked and found nothing, absent from one that does not report it.
   */
  game?: string | null;
  gameId?: string | null;
  /**
   * The audio tracks the file carries, in stream order, named as the user named them in settings.
   * Absent when nothing knows the layout — an imported file, or a session recorded before one was
   * written. `index` is the position in the file, which is what the clip engine keys adjustments by;
   * the settings Guid is deliberately not on the wire, because it is not persisted per recording.
   */
  audioTracks?: { index: number; name: string }[];
  /**
   * The item's length in seconds, from its metadata record. A DECLARED length: good enough for a
   * chip on a card, never good enough to bound a clip segment — see player/clipModel.ts, which
   * documents a record declaring 100s in front of a 9.13s file.
   */
  durationSeconds?: number;
  /** The file's size in bytes, when the backend reports it. */
  fileSizeBytes?: number;
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
  /**
   * The H.264 encoder ids this machine's runtime actually registered. A SIBLING of `settings`, not
   * a field inside it: it is not a persisted setting but a property of the running machine, so it
   * is never written back by UpdateSettings.
   */
  availableEncoders?: string[] | null;
  /**
   * The primary display's pixel size. A SIBLING of `settings` for the same reason
   * `availableEncoders` is one: it describes the machine, not the configuration, so it must never be
   * written back by UpdateSettings. Null when the host could not read a display from the platform.
   */
  displayResolution?: { width: number; height: number } | null;
  /**
   * The monitors attached right now. A SIBLING of `settings` like the two above — machine, not
   * configuration — so UpdateSettings never writes it back.
   */
  availableDisplays?: DisplayInfo[] | null;
  /** Set when `capture.display` names a monitor that is not attached and the recorder fell back. */
  displayFallbackWarning?: DisplayFallbackWarning | null;
}

/** One monitor on the wire. `id` is the stable id `capture.display` stores. */
export interface DisplayInfo {
  id: string;
  name: string;
  width: number;
  height: number;
  primary: boolean;
}

export interface DisplayFallbackWarning {
  requestedId: string;
  /** From `capture.displayLabel`. */
  requestedLabel: string | null;
  usingId: string | null;
  usingLabel: string | null;
}

export interface RecordingState {
  recording: boolean;
  game?: GameInfo | null;
  /**
   * When the current recording started, in unix SECONDS, or null when nothing is recording. Present
   * so a UI that connects mid-session shows a true elapsed time rather than counting from the
   * moment it connected — which for an 8-hour recording is a confidently wrong number.
   */
  startedAt?: number | null;
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
  id: string;
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

/**
 * The `error` push content. Sent when a user action could not be persisted — e.g. a bookmark,
 * title or delete could not be saved because the recording folder is unwritable.
 */
export interface ErrorMessage {
  message: string;
}

export interface WarningMessage {
  message: string;
}

// ---------------------------------------------------------------------------
// Model training
// ---------------------------------------------------------------------------

export interface TrainingEventDefinition {
  id: number;
  name: string;
  type: 'Trigger' | 'Exclusion' | 'Subtractor';
  classId: number;
  bookmarkType?: string | null;
  lifetimeMs?: number | null;
  subtractsEventId?: number | null;
  screenRegionX?: number | null;
  screenRegionY?: number | null;
  screenRegionW?: number | null;
  screenRegionH?: number | null;
}

export interface TrainingLabel {
  classId: number;
  centerX: number;
  centerY: number;
  width: number;
  height: number;
}

export interface TrainingSample {
  id: string;
  imageFile: string;
  sourcePath: string;
  timestampSeconds: number;
  imageWidth: number;
  imageHeight: number;
  labels: TrainingLabel[];
}

export interface TrainingModelInfo {
  inputWidth?: number | null;
  inputHeight?: number | null;
  classCount?: number | null;
  classNames?: Record<string, string> | null;
}

export interface TrainingMessage {
  gameId: string | null;
  revision?: string;
  events: TrainingEventDefinition[];
  samples: TrainingSample[];
  dataset?: {
    trainingImages: number;
    validationImages: number;
  };
  model?: TrainingModelInfo | null;
  trainingActive?: boolean;
}

export type TrainingProgressStatus =
  | 'started'
  | 'progress'
  | 'imported'
  | 'sampleSaved'
  | 'sampleUpdated'
   | 'sampleDeleted'
  | 'eventsUpdated'
  | 'completed'
  | 'cancelled'
  | 'error';

export interface TrainingProgressMessage {
  gameId: string;
  status: TrainingProgressStatus;
  message: string;
}

export interface TrainingSampleMessage {
  gameId: string;
  sample: TrainingSample;
  imageData: string;
  requestId?: string | null;
}

export interface TrainingSamplePreviewMessage {
  gameId: string;
  sample: TrainingSample;
  imageData: string;
  requestId?: string | null;
}

export interface TrainingLabelSuggestion {
  label: TrainingLabel;
  confidence: number;
}

export interface TrainingLabelSuggestionsMessage {
  gameId: string;
  sampleId: string;
  suggestions: TrainingLabelSuggestion[];
  requestId?: string | null;
}

// ---------------------------------------------------------------------------
// Trash
// ---------------------------------------------------------------------------

/**
 * One item sitting in the trash. `id` is opaque and only stable while the entry exists — a restored
 * and re-deleted item may come back under a different one.
 */
export interface TrashEntry {
  id: string;
  contentType: ContentType;
  /** The original file name, e.g. session-20260818-101112123.mp4. */
  fileName: string;
  title?: string;
  game?: string | null;
  durationSeconds?: number;
  fileSizeBytes?: number;
  /** Epoch SECONDS, like every other time on this wire. */
  deletedAt: number;
  /** Epoch SECONDS, or 0 when retention is disabled and nothing will auto-purge. */
  purgeAt: number;
}

/** The `trash` push content: the whole trash, plus how long the backend keeps an entry. */
export interface TrashMessage {
  entries: TrashEntry[];
  /** Hours; <= 0 means entries are never auto-purged. */
  retentionHours: number;
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
  /** The video's path RELATIVE to the content root (e.g. `sessions/session-1.mp4`), not the bare file name. */
  fileName: string;
  /** Omitted/false moves the item to the trash; true unlinks it immediately. */
  permanent?: boolean;
}

export interface DeleteMultipleContentParameters {
  items: DeleteContentParameters[];
  permanent?: boolean;
}

export interface RestoreTrashParameters {
  entryIds: string[];
}

export interface PurgeTrashParameters {
  /** Omitted empties the whole trash. */
  entryIds?: string[];
}

export interface RenameContentParameters {
  contentType: ContentType;
  fileName: string;
  title: string;
}

export interface ToggleFavoriteParameters {
  contentType: ContentType;
  filePath: string;
  favorite: boolean;
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

export interface TrainingGameParameters {
  gameId: string;
}

export interface ImportTrainingParameters {
  gameId: string;
  sourcePath: string;
  confirmOverwrite: boolean;
  expectedRevision?: string;
}

export interface CaptureTrainingSampleParameters {
  gameId: string;
  filePath: string;
  timestampSeconds: number;
  imageWidth: number;
  imageHeight: number;
  labels: TrainingLabel[];
}

export interface UpdateTrainingSampleParameters {
  gameId: string;
  sampleId: string;
  labels: TrainingLabel[];
}

export interface UpdateTrainingEventsParameters {
  gameId: string;
  events: TrainingEventDefinition[];
}

export interface TrainingSampleParameters {
  gameId: string;
  sampleId: string;
  previewOnly?: boolean;
  requestId?: string;
}

export interface SuggestTrainingLabelsParameters {
  gameId: string;
  sampleId: string;
  requestId?: string;
}

export interface StartTrainingParameters {
  gameId: string;
  imageSize?: number;
  epochs?: number;
  device?: string;
  baseModel?: string | null;
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
  | RestoreTrashParameters
  | PurgeTrashParameters
  | RenameContentParameters
  | ToggleFavoriteParameters
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
  | TrainingGameParameters
  | ImportTrainingParameters
  | CaptureTrainingSampleParameters
  | UpdateTrainingSampleParameters
  | UpdateTrainingEventsParameters
  | TrainingSampleParameters
  | SuggestTrainingLabelsParameters
  | StartTrainingParameters
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
  | 'ListGames'
  | 'BrowseTrainingFolder'
  | 'CreateClip'
  | 'CancelClip'
  | 'DeleteContent'
  | 'DeleteMultipleContent'
  | 'ListTrash'
  | 'RestoreTrash'
  | 'PurgeTrash'
  | 'RenameContent'
  | 'ToggleFavorite'
  | 'ImportFile'
  | 'AddBookmark'
  | 'DeleteBookmark'
  // Settings and presets
  | 'ListSettings'
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
  | 'RecoveryConfirm'
  // Model training
  | 'ListTraining'
  | 'ImportTrainingAssets'
  | 'CaptureTrainingSample'
  | 'GetTrainingSample'
  | 'UpdateTrainingSample'
  | 'SuggestTrainingLabels'
  | 'UpdateTrainingEvents'
  | 'DeleteTrainingSample'
  | 'StartTraining'
  | 'CancelTraining'
  | 'InstallTrainingModel';

/**
 * Backend → frontend messages, all lowercase. The reference contract split these between lowercase
 * (`settings`, `state`, `importProgress`) and PascalCase (`UpdateProgress`, `ReleaseNotes`,
 * `ShowModal`).
 */
export type MessageName =
  | 'settings'
  | 'state'
  | 'content'
  | 'trash'
  | 'importProgress'
  | 'updateProgress'
  | 'releaseNotes'
  | 'showReleaseNotes'
  | 'showModal'
  | 'storageWarning'
  | 'recoveryPrompt'
  | 'selectedGameExecutable'
  | 'gameList'
  | 'error'
  | 'warning'
  | 'training'
  | 'trainingProgress'
  | 'trainingSample'
  | 'trainingSamplePreview'
  | 'trainingLabelSuggestions'
  | 'trainingFolderSelected'
  | 'trainingFolderCancelled';
