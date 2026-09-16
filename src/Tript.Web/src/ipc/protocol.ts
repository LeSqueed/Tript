// SPDX-License-Identifier: GPL-2.0-or-later

import type { RecordingMode } from '../settings/settingsModel';

export interface CommandEnvelope {
  method: CommandName;
  parameters?: CommandParameters;
}

export interface MessageEnvelope {
  method: MessageName;
  content?: unknown;
}

export type ChangeCause = string;

export type ContentType = 'recording' | 'clip' | 'highlight' | 'buffer';

export interface ContentItem {
  contentType: ContentType;
  fileName: string;
  filePath: string;
  title?: string;
  favorite?: boolean;
  startTime?: number;
  endTime?: number;
  game?: string | null;
  gameId?: string | null;
  audioTracks?: { index: number; name: string }[];
  durationSeconds?: number;
  fileSizeBytes?: number;
  bookmarks?: BookmarkItem[];
  hasAutomaticClipCandidates?: boolean;
  automated?: boolean;
  sourceSessionPath?: string;
  clipStartTime?: number;
  clipEndTime?: number;
  automaticClipsProcessing?: boolean;
  automaticClipsPaused?: boolean;
  automaticClipsCompleted?: number;
  automaticClipsTotal?: number;
  videoMissing?: boolean;
  highlightsOnly?: boolean;
  recording?: boolean;
  isHdr?: boolean;
}

export interface BookmarkItem {
  id: string;
  type: string;
  subtype?: string;
  time: number;
  label?: string;
}

export interface GameInfo {
  id: string;
  name: string;
  detected: boolean;
  [key: string]: unknown;
}

export interface Settings {
  [key: string]: unknown;
}

export interface SettingsMessage {
  settings: Settings;
  cause?: ChangeCause;
  availableEncoders?: string[] | null;
  displayResolution?: { width: number; height: number } | null;
  availableDisplays?: DisplayInfo[] | null;
  displayFallbackWarning?: DisplayFallbackWarning | null;
  appVersion?: string | null;
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

export interface RecordingState {
  recording: boolean;
  activeRecordingMode?: RecordingMode | null;
  game?: GameInfo | null;
  activeModelGameId?: string | null;
  startedAt?: number | null;
  automaticClips?: {
    active: boolean;
    paused: boolean;
    sourceSessionPath: string;
    completed: number;
    total: number;
  } | null;
  audioTracks?: { id: string; device: string; muted: boolean; volume: number }[];
  [key: string]: unknown;
}

export interface StateMessage {
  state: RecordingState;
  cause?: ChangeCause;
}

export type GameModelStage =
  | 'checking'
  | 'downloading'
  | 'verifying'
  | 'installing'
  | 'ready'
  | 'error'
  | 'unsupported';

export interface GameModelStatus {
  gameId: string;
  stage: GameModelStage;
  revision?: number;
  completedBytes?: number;
  totalBytes?: number;
  message?: string;
}

export interface ModelStatusMessage {
  models: GameModelStatus[];
}

export interface AudioLevelsMessage {
  levels: { deviceId: string; peak: number }[];
}

export interface WindowVisibilityMessage {
  visible: boolean;
}

export interface AvailableRecordingModel {
  gameId: string;
  name: string;
}

export interface AvailableRecordingModelsMessage {
  models: AvailableRecordingModel[];
}

export interface ImportProgressMessage {
  id: string;
  status: 'importing' | 'done' | 'error';
  content?: ContentItem;
  error?: string;
}

export type UpdateStage = 'idle' | 'checking' | 'upToDate' | 'available' | 'downloading' | 'ready' | 'error';

export interface UpdateProgressMessage {
  stage: UpdateStage;
  version?: string;
  releaseUrl?: string;
  completedBytes?: number;
  totalBytes?: number;
  error?: string;
}

export interface SelectedGameExecutableMessage {
  requestId: string;
  filePath: string | null;
}

export interface GameSearchResult {
  gameId?: string | null;
  name: string;
  year?: number | null;
  platforms?: string | null;
  source: string;
  steamAppId?: number | null;
  igdbId?: number | null;
}

export interface GameSearchResultsMessage {
  requestId: string;
  results: GameSearchResult[];
  error?: string | null;
}

export interface ResolvedGameSearchMessage {
  requestId: string;
  game?: { gameId: string; name: string } | null;
  error?: string | null;
}

export interface SettingsUpdateResultMessage {
  requestId: string;
  success: boolean;
  error?: string | null;
}

export interface GameCandidateMessage {
  pid: number;
  executable: string;
  executablePath: string;
}

export interface GameCandidateActionResultMessage {
  requestId: string;
  executablePath: string;
  action: 'add' | 'ignore';
  success: boolean;
  error?: string | null;
}

export interface ErrorMessage {
  message: string;
}

export interface WarningMessage {
  message: string;
}

export interface TrainingEventDefinition {
  id: number;
  name: string;
  type: 'Trigger' | 'Exclusion' | 'Subtractor';
  detectionKind?: 'Object' | 'Ocr';
  classId: number;
  ocr?: TrainingOcrEventDefinition | null;
  bookmarkType?: string | null;
  includeInAutoClips?: boolean;
  subtractsEventId?: number | null;
  regionGroupId?: number | null;
  screenRegionX?: number | null;
  screenRegionY?: number | null;
  screenRegionW?: number | null;
  screenRegionH?: number | null;
  fixedPosition?: boolean;
  fixedLabelCenterX?: number | null;
  fixedLabelCenterY?: number | null;
  fixedLabelWidth?: number | null;
  fixedLabelHeight?: number | null;
}

export interface TrainingOcrPattern {
  languageTag: string;
  template: string;
  maximumEditDistance?: number;
  minimumScore?: number;
}

export interface TrainingOcrSegment {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface TrainingOcrEventDefinition {
  patterns: TrainingOcrPattern[];
  segments?: TrainingOcrSegment[];
  minimumConfidence?: number;
  tracking?: {
    confirmationFrames?: number;
    minimumStableMilliseconds?: number;
    expireAfterMissingMilliseconds?: number;
    maximumTextDistance?: number;
    minimumBoundsIou?: number;
  };
}

export interface GameAddedMessage {
  gameId: string;
  name: string;
  executablePath: string;
}

export interface TrainingRegionGroup {
  id: number;
  name: string;
  screenRegionX: number | null;
  screenRegionY: number | null;
  screenRegionW: number | null;
  screenRegionH: number | null;
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
  ocrRegions?: TrainingOcrRegion[];
}

export interface TrainingModelInfo {
  inputWidth?: number | null;
  inputHeight?: number | null;
  classCount?: number | null;
  classNames?: Record<string, string> | null;
}

export interface TrainingEventCoverage {
  classId: number;
  name: string;
  sampleCount: number;
  trainingSamples: number;
  validationSamples: number;
}

export interface TrainingMessage {
  gameId: string | null;
  revision?: string;
  events: TrainingEventDefinition[];
  samples: TrainingSample[];
  regionGroups?: TrainingRegionGroup[];
  invalidSamples?: Array<{ id: string; reason: string }>;
  dataset?: {
    trainingImages: number;
    validationImages: number;
    eventCoverage: TrainingEventCoverage[];
    warnings: string[];
  };
  model?: TrainingModelInfo | null;
  trainingActive?: boolean;
  trainingPhase?: 'exporting' | 'training' | null;
  preferences?: {
    epochs: number; device: string; augmentCopies: number;
    ocrEpochs?: number;
  } | null;
}

export type TrainingProgressStatus =
  | 'exporting'
  | 'progress'
  | 'imported'
  | 'sampleSaved'
  | 'sampleUpdated'
  | 'sampleDeleted'
  | 'eventsUpdated'
  | 'eventDeleteProgress'
  | 'regionGroupsUpdated'
  | 'completed'
  | 'cancelled'
  | 'error';

export interface TrainingProgressMessage {
  gameId: string;
  status: TrainingProgressStatus;
  message: string;
  percent?: number | null;
  requestId?: string | null;
  details?: { epoch: number; epochs: number; loss: number | null; map50: number | null } | null;
}

export interface TrainingUpdateResultMessage {
  requestId: string;
  success: boolean;
  error?: string | null;
}

export interface TrainingPublishResultMessage {
  requestId: string;
  success: boolean;
  revision?: number;
  error?: string | null;
}

export interface TrainingPushMessage {
  training: TrainingMessage;
  requestId?: string | null;
  updateKind?: 'events' | 'regionGroups' | null;
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

export interface TrashEntry {
  id: string;
  contentType: ContentType;
  fileName: string;
  title?: string;
  game?: string | null;
  durationSeconds?: number;
  fileSizeBytes?: number;
  deletedAt: number;
  purgeAt: number;
}

export interface TrashMessage {
  entries: TrashEntry[];
  retentionHours: number;
}

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

export interface CreateAutomaticClipsParameters {
  filePath: string;
}

export interface StartRecordingParameters {
  gameId?: string;
  applyDisplay?: boolean;
  displayId?: string | null;
}

export interface ConvertToSdrParameters {
  id: string;
  contentType: 'clip' | 'highlight';
  filePath: string;
}

export interface ClipSegment {
  startTime: number;
  endTime: number;
}

export interface DeleteContentParameters {
  contentType: ContentType;
  fileName: string;
  permanent?: boolean;
  deleteLinkedHighlights?: boolean;
}

export interface DeleteMultipleContentParameters {
  items: DeleteContentParameters[];
  permanent?: boolean;
}

export interface RestoreTrashParameters {
  entryIds: string[];
}

export interface PurgeTrashParameters {
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

export interface UpdateSettingsParameters {
  settings: Partial<Settings>;
  requestId: string;
}

export interface SelectGameExecutableParameters {
  requestId: string;
}

export interface SearchGamesParameters {
  requestId: string;
  query: string;
  limit?: number;
}

export interface ResolveGameSearchParameters {
  requestId: string;
  input: string;
}

export interface RequestGameAddParameters {
  requestId: string;
  gameId: string;
}

export interface GameAddRequestedMessage {
  requestId: string;
  gameId: string;
  status: 'accepted' | 'alreadyRequested' | 'rateLimited' | 'rejected';
  retryAfterSeconds?: number;
  error?: string;
}

export interface AddGameCandidateParameters {
  requestId: string;
  name?: string;
  executablePath: string;
}

export interface IgnoreGameCandidateParameters {
  requestId: string;
  executablePath: string;
}

export interface OpenFileLocationParameters {
  filePath: string;
}

export interface OpenInBrowserParameters {
  url: string;
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
  requestId: string;
  labels: TrainingLabel[];
  ocrRegions?: TrainingOcrRegion[];
}

export interface TrainingOcrRegion {
  x: number;
  y: number;
  width: number;
  height: number;
  text: string;
}

export interface UpdateTrainingEventsParameters {
  gameId: string;
  requestId: string;
  events: TrainingEventDefinition[];
}

export interface UpdateTrainingRegionGroupsParameters {
  gameId: string;
  requestId: string;
  regionGroups: TrainingRegionGroup[];
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
  augmentCopies?: number;
  scope?: 'all' | 'object' | 'ocr';
  ocrEpochs?: number;
}

export interface PublishTrainingModelParameters {
  requestId: string;
  gameId: string;
  username: string;
  password: string;
}

export interface NewConnectionParameters {
  protocolVersion: number;
}

export type CommandParameters =
  | StartRecordingParameters
  | CreateClipParameters
  | CreateAutomaticClipsParameters
  | ConvertToSdrParameters
  | DeleteContentParameters
  | DeleteMultipleContentParameters
  | RestoreTrashParameters
  | PurgeTrashParameters
  | RenameContentParameters
  | ToggleFavoriteParameters
  | AddBookmarkParameters
  | DeleteBookmarkParameters
  | UpdateSettingsParameters
  | SelectGameExecutableParameters
  | SearchGamesParameters
  | ResolveGameSearchParameters
  | RequestGameAddParameters
  | AddGameCandidateParameters
  | IgnoreGameCandidateParameters
  | OpenFileLocationParameters
  | OpenInBrowserParameters
  | TrainingGameParameters
  | ImportTrainingParameters
  | CaptureTrainingSampleParameters
  | UpdateTrainingSampleParameters
  | UpdateTrainingEventsParameters
  | UpdateTrainingRegionGroupsParameters
  | TrainingSampleParameters
  | SuggestTrainingLabelsParameters
  | StartTrainingParameters
  | PublishTrainingModelParameters
  | NewConnectionParameters;

export type CommandName =
  | 'StartRecording'
  | 'StopRecording'
  | 'NewConnection'
  | 'CheckForUpdates'
  | 'ApplyUpdate'
  | 'ListContent'
  | 'ListGames'
  | 'BrowseTrainingFolder'
  | 'CreateClip'
  | 'CreateAutomaticClips'
  | 'ConvertToSdr'
  | 'PauseAutomaticClips'
  | 'DeleteContent'
  | 'DeleteMultipleContent'
  | 'ListTrash'
  | 'WatchAudioLevels'
  | 'RestoreTrash'
  | 'PurgeTrash'
  | 'RenameContent'
  | 'ToggleFavorite'
  | 'AddBookmark'
  | 'DeleteBookmark'
  | 'ListSettings'
  | 'UpdateSettings'
  | 'SetVideoLocation'
  | 'SelectGameExecutable'
  | 'SearchGames'
  | 'ResolveGameSearch'
  | 'RequestGameAdd'
  | 'AddGameCandidate'
  | 'IgnoreGameCandidate'
  | 'OpenFileLocation'
  | 'OpenInBrowser'
  | 'ListTraining'
  | 'ImportTrainingAssets'
  | 'CaptureTrainingSample'
  | 'GetTrainingSample'
  | 'UpdateTrainingSample'
  | 'SuggestTrainingLabels'
  | 'UpdateTrainingEvents'
  | 'UpdateTrainingRegionGroups'
  | 'DeleteTrainingSample'
  | 'StartTraining'
  | 'CancelTraining'
  | 'InstallTrainingModel'
  | 'PublishTrainingModel'
  | 'ListAvailableRecordingModels'
  | 'ActivateRecordingModel';

export type MessageName =
  | 'settings'
  | 'state'
  | 'audioLevels'
  | 'windowVisibility'
  | 'modelStatus'
  | 'availableRecordingModels'
  | 'content'
  | 'trash'
  | 'importProgress'
  | 'updateProgress'
  | 'selectedGameExecutable'
  | 'gameSearchResults'
  | 'gameSearchResolved'
  | 'gameAddRequested'
  | 'settingsUpdateResult'
  | 'gameCandidate'
  | 'gameCandidateCleared'
  | 'gameCandidateActionResult'
  | 'gameAdded'
  | 'gameList'
  | 'error'
  | 'warning'
  | 'training'
  | 'trainingProgress'
  | 'trainingPublishResult'
  | 'trainingEventsUpdateResult'
  | 'trainingRegionGroupsUpdateResult'
  | 'trainingSampleUpdateResult'
  | 'trainingSample'
  | 'trainingSamplePreview'
  | 'trainingLabelSuggestions'
  | 'trainingFolderSelected'
  | 'trainingFolderCancelled';
