import { useEffect, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import { Button, Field, SelectField, TextField } from './ui/controls';
import { LoadingOverlay } from './ui/LoadingOverlay';
import { TrainingSampleEditor } from './TrainingSampleEditor';
import { TrainingEventEditor } from './TrainingEventEditor';
import { TrainingEventTree } from './TrainingEventTree';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import type {
  GameInfo,
  TrainingEventDefinition,
  TrainingMessage,
  TrainingProgressMessage,
  TrainingPublishResultMessage,
  TrainingPushMessage,
  TrainingRegionGroup,
  TrainingSample,
  TrainingSampleMessage,
  TrainingSamplePreviewMessage,
  TrainingUpdateResultMessage,
} from '../ipc/protocol';

interface TrainingViewProps {
  client: IpcClient;
}

const EMPTY_TRAINING: TrainingMessage = { gameId: null, events: [], samples: [], regionGroups: [], invalidSamples: [] };
const SAMPLE_PAGE_SIZE = 8;

// One per-epoch heartbeat (loss/mAP50 plus arrival time) for the in-progress panel: it feeds the
// sparkline and the elapsed/remaining pacing derived from when each epoch finished.
interface TrainingEpochPoint {
  epoch: number;
  loss: number | null;
  map50: number | null;
  receivedAt: number;
}

function formatDuration(totalMs: number): string {
  const totalSeconds = Math.max(0, Math.round(totalMs / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes === 0 ? `${seconds}s` : `${minutes}m ${String(seconds).padStart(2, '0')}s`;
}

function trainingPace(history: TrainingEpochPoint[], totalEpochs: number): { elapsedMs: number; remainingMs: number | null } {
  if (history.length < 2) return { elapsedMs: 0, remainingMs: null };
  const last = history[history.length - 1];
  const intervals = history.slice(1).map((point, index) => {
    const previous = history[index];
    return Math.max(0, point.receivedAt - previous.receivedAt) / Math.max(1, point.epoch - previous.epoch);
  }).slice(-7).sort((left, right) => left - right);
  const middle = Math.floor(intervals.length / 2);
  const perEpochMs = intervals.length % 2 === 0
    ? (intervals[middle - 1] + intervals[middle]) / 2
    : intervals[middle];
  return {
    elapsedMs: perEpochMs * last.epoch,
    remainingMs: perEpochMs * Math.max(0, totalEpochs - last.epoch),
  };
}

// Each series is min/max-normalized on its own scale and aligned by epoch, so epochs without
// validation metrics leave a gap instead of shifting the line.
function TrainingSparkline({ points, totalEpochs }: { points: TrainingEpochPoint[]; totalEpochs: number }) {
  const width = 100;
  const height = 40;
  const pad = 4;
  const xFor = (epoch: number) => (totalEpochs <= 1 ? width / 2 : ((epoch - 1) / (totalEpochs - 1)) * width);
  const seriesFor = (pick: (point: TrainingEpochPoint) => number | null) => {
    const series = points.filter((point) => pick(point) != null);
    if (series.length === 0) return null;
    const values = series.map((point) => pick(point) as number);
    const min = Math.min(...values);
    const span = Math.max(...values) - min;
    const toCoord = (point: TrainingEpochPoint, x?: number) => {
      const normalized = span === 0 ? 0.5 : ((pick(point) as number) - min) / span;
      return { x: x ?? xFor(point.epoch), y: pad + (1 - normalized) * (height - pad * 2) };
    };
    const coords = series.map((point) => toCoord(point));
    const drawn = coords.length === 1
      ? [{ x: coords[0].x - 1.5, y: coords[0].y }, { x: coords[0].x + 1.5, y: coords[0].y }]
      : coords;
    return {
      line: drawn.map((c) => `${c.x.toFixed(2)},${c.y.toFixed(2)}`).join(' '),
      first: coords[0],
      last: coords[coords.length - 1],
    };
  };
  const loss = seriesFor((point) => point.loss);
  const map = seriesFor((point) => point.map50);
  if (!loss && !map) return null;
  const grid = [0.25, 0.5, 0.75].map((f) => pad + f * (height - pad * 2));
  const lossArea = loss
    ? `${loss.first.x.toFixed(2)},${height - pad} ${loss.line} ${loss.last.x.toFixed(2)},${height - pad}`
    : null;
  return (
    <svg
      className="training-sparkline"
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
      role="img"
      aria-label="Training metrics per epoch"
    >
      {grid.map((y) => <line key={y} className="training-sparkline-grid" x1={0} x2={width} y1={y} y2={y} />)}
      <line className="training-sparkline-grid training-sparkline-baseline" x1={0} x2={width} y1={height - pad} y2={height - pad} />
      {lossArea && <polygon className="training-sparkline-fill" points={lossArea} />}
      {map && <polyline className="training-sparkline-map" points={map.line} />}
      {loss && <polyline className="training-sparkline-loss" points={loss.line} />}
      {map && (
        <line
          className="training-sparkline-tick training-sparkline-map"
          x1={map.last.x} x2={map.last.x} y1={map.last.y - 2} y2={map.last.y + 2}
        />
      )}
      {loss && (
        <line
          className="training-sparkline-tick training-sparkline-loss"
          x1={loss.last.x} x2={loss.last.x} y1={loss.last.y - 2} y2={loss.last.y + 2}
        />
      )}
    </svg>
  );
}

export function TrainingView({ client }: TrainingViewProps) {
  const [games, setGames] = useState<GameInfo[]>([]);
  const [training, setTraining] = useState<TrainingMessage>(EMPTY_TRAINING);
  const [gameId, setGameId] = useState('');
  const [sourcePath, setSourcePath] = useState('');
  const [folderPickerStatus, setFolderPickerStatus] = useState<'idle' | 'selected' | 'cancelled'>('idle');
  const [epochs, setEpochs] = useState(100);
  const [device, setDevice] = useState('auto');
  const [augmentCopies, setAugmentCopies] = useState(0);
  const [trainingScope, setTrainingScope] = useState<'all' | 'object' | 'ocr'>('all');
  const [progress, setProgress] = useState<TrainingProgressMessage | null>(null);
  const [epochHistory, setEpochHistory] = useState<TrainingEpochPoint[]>([]);
  // Latest non-epoch runner message (export console lines, coverage summary, ONNX export note),
  // shown inside the in-progress panel while the run is active.
  const [statusNote, setStatusNote] = useState<string | null>(null);
  const [selectedSample, setSelectedSample] = useState<TrainingSampleMessage | null>(null);
  const [eventEditor, setEventEditor] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionEditor, setRegionEditor] = useState<
    { target: TrainingEventDefinition; targetType: 'event'; sampleId?: string }
    | { target: TrainingRegionGroup; targetType: 'group'; sampleId?: string }
    | null
  >(null);
  const [eventError, setEventError] = useState<string | null>(null);
  const [samplePreviews, setSamplePreviews] = useState<Record<string, string>>({});
  const [previewErrors, setPreviewErrors] = useState<Record<string, boolean>>({});
  const [sampleFilter, setSampleFilter] = useState('');
  const [sampleValidity, setSampleValidity] = useState<'all' | 'invalid' | 'valid'>('all');
  const [newGroupName, setNewGroupName] = useState('');
  const [samplePage, setSamplePage] = useState(1);
  const [isImporting, setIsImporting] = useState(false);
  const [deletingEventName, setDeletingEventName] = useState<string | null>(null);
  const [deletePercent, setDeletePercent] = useState<number | null>(null);
  const [preparingDataset, setPreparingDataset] = useState(false);
  const [installedRegionsStale, setInstalledRegionsStale] = useState(false);
  const [adminUsername, setAdminUsername] = useState('admin');
  const [adminPassword, setAdminPassword] = useState('');
  const [publishRequestId, setPublishRequestId] = useState<string | null>(null);
  const [publishResult, setPublishResult] = useState<string | null>(null);
  const pendingEventsRef = useRef<{ gameId: string; requestId: string; events: TrainingEventDefinition[] } | null>(null);
  const pendingRegionGroupsRef = useRef<{
    gameId: string; requestId: string; regionGroups: TrainingRegionGroup[];
  } | null>(null);
  const latestEventsRequestRef = useRef<string | null>(null);
  const latestRegionGroupsRequestRef = useRef<string | null>(null);
  const editRequestCounterRef = useRef(0);
  const importingRef = useRef(false);
  const loadedTrainingGameIdRef = useRef<string | null>(null);
  const activeGameIdRef = useRef(gameId);
  const previewRequestsRef = useRef(new Map<string, string>());
  const previewRetryCountRef = useRef(new Map<string, number>());
  const previewRequestCounterRef = useRef(0);
  const selectedSampleRequestRef = useRef<string | null>(null);
  const publishRequestRef = useRef<string | null>(null);
  activeGameIdRef.current = gameId;
  const isWindows = typeof navigator !== 'undefined' && /Windows/i.test(navigator.userAgent);
  const sourcePlaceholder = isWindows
    ? String.raw`C:\Users\You\Documents\Tript training`
    : '/home/you/tript-training';

  useEffect(() => {
    const removeGames = client.on('gameList', (content) => {
      const nextGames = Array.isArray(content) ? (content as GameInfo[]) : [];
      setGames(nextGames);
      setGameId((current) => current || nextGames[0]?.id || '');
    });
    const removeTraining = client.on('training', (content) => {
      const push = content as Partial<TrainingPushMessage>;
      const message = push.training;
      if (message) {
        if (message.gameId && message.gameId !== activeGameIdRef.current) return;
        if (push.updateKind === 'events' && push.requestId !== latestEventsRequestRef.current) return;
        if (push.updateKind === 'regionGroups' && push.requestId !== latestRegionGroupsRequestRef.current) return;
        if (push.updateKind === 'events') pendingEventsRef.current = null;
        if (push.updateKind === 'regionGroups') pendingRegionGroupsRef.current = null;
        // Only the first push for a game restores the form; later re-pushes (samples, progress)
        // must not clobber what the user is currently editing.
        const freshLoad = loadedTrainingGameIdRef.current !== message.gameId;
        loadedTrainingGameIdRef.current = message.gameId;
        setTraining({
          ...message,
          events: pendingEventsRef.current?.gameId === message.gameId
            ? pendingEventsRef.current.events : message.events,
          regionGroups: pendingRegionGroupsRef.current?.gameId === message.gameId
            ? pendingRegionGroupsRef.current.regionGroups : message.regionGroups ?? [],
          invalidSamples: message.invalidSamples ?? [],
        });
        if (message.gameId) {
          setGameId(message.gameId);
          if (freshLoad) {
            setEpochs(message.preferences?.epochs ?? 100);
            setDevice(message.preferences?.device ?? 'auto');
            setAugmentCopies(message.preferences?.augmentCopies ?? 0);
          }
        }
      }
    });
    const removeProgress = client.on('trainingProgress', (content) => {
      const message = content as TrainingProgressMessage;
      if (message.gameId !== activeGameIdRef.current) return;
      if ((message.status === 'eventsUpdated' || message.status === 'eventDeleteProgress')
        && message.requestId !== latestEventsRequestRef.current) return;
      if (message.status === 'regionGroupsUpdated'
        && message.requestId !== latestRegionGroupsRequestRef.current) return;
      setProgress(message);
      if (message.details && message.details.epoch > 0) {
        setPreparingDataset(false);
        const details = message.details;
        setEpochHistory((current) => {
          const point: TrainingEpochPoint = {
            epoch: details.epoch,
            loss: details.loss,
            map50: details.map50,
            receivedAt: Date.now(),
          };
          return [...current.filter((entry) => entry.epoch !== point.epoch), point]
            .sort((a, b) => a.epoch - b.epoch);
        });
      } else if (message.status === 'exporting') {
        setPreparingDataset(true);
        setEpochHistory([]);
        setStatusNote(message.message);
      } else if (message.status === 'progress') {
        setStatusNote(message.message);
      }
      if (message.status === 'eventsUpdated') {
        setDeletingEventName(null);
        setDeletePercent(null);
      }
      if (message.status === 'eventDeleteProgress') {
        setDeletePercent(message.percent ?? null);
      }
      if (message.status === 'imported') {
        setInstalledRegionsStale(false);
        importingRef.current = false;
        setIsImporting(false);
        setSelectedSample(null);
        setSamplePreviews({});
        setPreviewErrors({});
        previewRequestsRef.current.clear();
        previewRetryCountRef.current.clear();
        selectedSampleRequestRef.current = null;
        setSamplePage(1);
        client.send('ListTraining', { gameId: message.gameId });
      }
      if (message.status === 'completed') setInstalledRegionsStale(false);
      if (message.status === 'completed' || message.status === 'cancelled' || message.status === 'error') {
        setPreparingDataset(false);
      }
    });
    const removeError = client.on('error', () => {
      if (importingRef.current) {
        importingRef.current = false;
        setIsImporting(false);
        if (activeGameIdRef.current) client.send('ListTraining', { gameId: activeGameIdRef.current });
      }
    });
    const removePublishResult = client.on('trainingPublishResult', (content) => {
      const result = content as Partial<TrainingPublishResultMessage>;
      if (result.requestId !== publishRequestRef.current) return;
      publishRequestRef.current = null;
      setPublishRequestId(null);
      setAdminPassword('');
      setPublishResult(result.success
        ? `Published revision ${result.revision}.`
        : result.error || 'The trained model could not be published.');
    });
    const removeEventsResult = client.on('trainingEventsUpdateResult', (content) => {
      const result = content as TrainingUpdateResultMessage;
      if (result.requestId !== latestEventsRequestRef.current) return;
      setDeletingEventName(null);
      setDeletePercent(null);
      if (result.success) {
        setEventError(null);
        return;
      }
      pendingEventsRef.current = null;
      setEventError(result.error ?? 'Could not update training events.');
      if (activeGameIdRef.current) client.send('ListTraining', { gameId: activeGameIdRef.current });
    });
    const removeRegionGroupsResult = client.on('trainingRegionGroupsUpdateResult', (content) => {
      const result = content as TrainingUpdateResultMessage;
      if (result.requestId !== latestRegionGroupsRequestRef.current) return;
      if (result.success) {
        setEventError(null);
        return;
      }
      pendingRegionGroupsRef.current = null;
      setEventError(result.error ?? 'Could not update training region groups.');
      if (activeGameIdRef.current) client.send('ListTraining', { gameId: activeGameIdRef.current });
    });
    const removeSample = client.on('trainingSample', (content) => {
      const message = content as TrainingSampleMessage;
      if (message.requestId && message.requestId !== selectedSampleRequestRef.current) return;
      if (message.gameId === activeGameIdRef.current) setSelectedSample(message);
    });
    const removePreview = client.on('trainingSamplePreview', (content) => {
      const preview = content as TrainingSamplePreviewMessage;
      if (preview.gameId !== activeGameIdRef.current) return;
      if (preview.requestId && previewRequestsRef.current.get(preview.sample.id) !== preview.requestId) return;
      previewRequestsRef.current.delete(preview.sample.id);
      previewRetryCountRef.current.delete(preview.sample.id);
      setPreviewErrors((current) => {
        if (!current[preview.sample.id]) return current;
        const next = { ...current };
        delete next[preview.sample.id];
        return next;
      });
      setSamplePreviews((current) => ({ ...current, [preview.sample.id]: preview.imageData }));
    });
    const removeFolder = client.on('trainingFolderSelected', (content) => {
      const path = (content as { path?: unknown }).path;
      if (typeof path === 'string') {
        setSourcePath(path);
        setFolderPickerStatus('selected');
      }
    });
    const removeFolderCancelled = client.on('trainingFolderCancelled', () => {
      setFolderPickerStatus('cancelled');
    });
    const removeConnection = client.onStateChange((state) => {
      if (state === 'connected') return;
      setDeletingEventName(null);
      setDeletePercent(null);
      setPreparingDataset(false);
    });
    // The initial gameList push can happen before this route mounts. Request it again so the
    // training picker is populated when the user navigates here later.
    client.send('ListGames');
    return () => {
      removeGames();
      removeTraining();
      removeProgress();
      removePublishResult();
      removeError();
      removeEventsResult();
      removeRegionGroupsResult();
      removeSample();
      removePreview();
      removeFolder();
      removeFolderCancelled();
      removeConnection();
    };
  }, [client]);

  useEffect(() => {
    if (gameId) {
      loadedTrainingGameIdRef.current = null;
      client.send('ListTraining', { gameId });
      setSelectedSample(null);
      setProgress(null);
      setPreparingDataset(false);
      setEpochHistory([]);
      setStatusNote(null);
      setInstalledRegionsStale(false);
      setSamplePreviews({});
      setPreviewErrors({});
      previewRequestsRef.current.clear();
      previewRetryCountRef.current.clear();
      selectedSampleRequestRef.current = null;
      pendingEventsRef.current = null;
      pendingRegionGroupsRef.current = null;
      latestEventsRequestRef.current = null;
      latestRegionGroupsRequestRef.current = null;
      setSamplePage(1);
    }
  }, [client, gameId]);

  const importAssets = () => {
    if (!gameId || !sourcePath.trim() || isImporting || loadedTrainingGameIdRef.current !== gameId) return;
    const hasExistingData = training.events.length > 0 || training.samples.length > 0;
    if (hasExistingData && !window.confirm(
      'This training workspace already contains data. Importing will replace events.json and the imported model, add full-resolution samples, and remove the current model if the import has no model. Prepared dataset images are ignored; existing captured samples are preserved. Continue?',
    )) return;

    importingRef.current = true;
    setIsImporting(true);
    client.send('ImportTrainingAssets', {
      gameId,
      sourcePath: sourcePath.trim(),
      confirmOverwrite: true,
      expectedRevision: training.revision,
    });
  };

  const regionGroups = training.regionGroups ?? [];
  const invalidSamples = training.invalidSamples ?? [];
  const invalidById = new Map(invalidSamples.map((sample) => [sample.id, sample.reason]));
  const validLabeledSampleCount = training.samples.filter((sample) =>
    (sample.labels.length > 0 || (sample.ocrRegions?.length ?? 0) > 0)
      && !invalidById.has(sample.id)).length;
  const canPickScope = training.events.some((event) => (event.detectionKind ?? 'Object') === 'Object')
    && training.events.some((event) => event.detectionKind === 'Ocr');
  const trainingIsActive = training.trainingActive
    || progress?.status === 'exporting' || progress?.status === 'progress';
  // The dataset-prep phase locks the workspace, so it gets the modal — including for a client that
  // connected mid-run and learned the phase from the training push.
  const exportingDataset = preparingDataset || training.trainingPhase === 'exporting';
  const epochDetails = progress?.details && progress.details.epoch > 0 ? progress.details : null;
  const pace = trainingPace(epochHistory, epochDetails?.epochs ?? epochs);
  // A series only earns its legend entry once a real value arrives; a run whose metrics never
  // populate (e.g. DirectML skips validation) must not show an empty graph frame.
  const hasLoss = epochHistory.some((point) => point.loss != null);
  const hasMap50 = epochHistory.some((point) => point.map50 != null);
  const normalizedSampleFilter = sampleFilter.trim().toLowerCase();
  const filteredSamples = training.samples.filter((sample) => {
    const isInvalid = invalidById.has(sample.id);
    if (sampleValidity === 'invalid' && !isInvalid) return false;
    if (sampleValidity === 'valid' && isInvalid) return false;
    if (!normalizedSampleFilter) return true;
    const labelNames = sample.labels.map((label) => training.events.find((event) => event.classId === label.classId)?.name ?? String(label.classId));
    const ocrText = (sample.ocrRegions ?? []).map((region) => region.text);
    return [sample.id, sample.timestampSeconds.toFixed(2), ...labelNames, ...ocrText]
      .some((value) => value.toLowerCase().includes(normalizedSampleFilter));
  });
  const samplePageCount = Math.max(1, Math.ceil(filteredSamples.length / SAMPLE_PAGE_SIZE));
  const pageSamples = filteredSamples.slice((samplePage - 1) * SAMPLE_PAGE_SIZE, samplePage * SAMPLE_PAGE_SIZE);
  const pageSampleIds = pageSamples.map((sample) => sample.id).join('|');
  const eventCoverage = new Map(
    training.dataset?.eventCoverage.map((coverage) => [coverage.classId, coverage]) ?? [],
  );
  const regionPreviewSample = regionEditor?.sampleId
    ? training.samples.find((sample) => sample.id === regionEditor.sampleId)
    : undefined;

  const requestPreview = (sample: TrainingSample) => {
    if (!gameId) return;
    const requestId = `${++previewRequestCounterRef.current}-${sample.id}`;
    previewRequestsRef.current.set(sample.id, requestId);
    client.send('GetTrainingSample', { gameId, sampleId: sample.id, previewOnly: true, requestId });
  };

  const retryPreview = (sample: TrainingSample) => {
    const attempts = (previewRetryCountRef.current.get(sample.id) ?? 0) + 1;
    previewRetryCountRef.current.set(sample.id, attempts);
    if (attempts <= 2) {
      requestPreview(sample);
      return;
    }
    previewRequestsRef.current.delete(sample.id);
    setPreviewErrors((current) => ({ ...current, [sample.id]: true }));
  };

  useEffect(() => {
    if (!gameId || !pageSampleIds) return;
    pageSamples.forEach((sample) => {
      if (!samplePreviews[sample.id] && !previewRequestsRef.current.has(sample.id)) requestPreview(sample);
    });
  }, [client, gameId, pageSampleIds, samplePage, samplePreviews]);

  useEffect(() => {
    if (samplePage > samplePageCount) setSamplePage(samplePageCount);
  }, [samplePage, samplePageCount]);

  const startTraining = () => {
    if (gameId) {
      client.send('StartTraining', {
        gameId, epochs, device, augmentCopies,
        scope: canPickScope ? trainingScope : 'all',
      });
    }
  };

  const openNewEvent = () => {
    const nextId = Math.max(0, ...training.events.map((event) => event.id)) + 1;
    const nextClassId = Math.max(-1, ...training.events
      .filter((event) => (event.detectionKind ?? 'Object') === 'Object')
      .map((event) => event.classId)) + 1;
    setEventEditor({
      event: {
        id: nextId,
        classId: nextClassId,
        name: 'New event',
        type: 'Trigger',
        detectionKind: 'Object',
        bookmarkType: 'Manual',
      },
      isNew: true,
    });
  };

  const saveEvent = (nextEvent: TrainingEventDefinition) => {
    if (!gameId) return;
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    const events = currentEvents.some((event) => event.id === nextEvent.id)
      ? currentEvents.map((event) => event.id === nextEvent.id ? nextEvent : event)
      : [...currentEvents, nextEvent];
    saveEvents(events);
    setEventError(null);
    setEventEditor(null);
  };

  const deleteEvent = (eventId: number) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError('Tript is not connected.');
      return;
    }
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    if (currentEvents.length <= 1) {
      setEventError('A training workspace must keep at least one event.');
      return;
    }
    const event = currentEvents.find((candidate) => candidate.id === eventId);
    setDeletingEventName(event?.name ?? 'event');
    setDeletePercent(0);
    saveEvents(currentEvents.filter((candidate) => candidate.id !== eventId));
  };

  const saveEvents = (events: TrainingEventDefinition[]) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError('Tript is not connected.');
      return;
    }
    setEventError(null);
    const regionFields: Array<keyof TrainingEventDefinition> = [
      'regionGroupId', 'screenRegionX', 'screenRegionY', 'screenRegionW', 'screenRegionH',
    ];
    const regionsChanged = events.length !== training.events.length || events.some((event) => {
      const previous = training.events.find((candidate) => candidate.id === event.id);
      return !previous || regionFields.some((field) => previous[field] !== event[field]);
    });
    if (training.model && regionsChanged) setInstalledRegionsStale(true);
    const requestId = `events-${++editRequestCounterRef.current}`;
    latestEventsRequestRef.current = requestId;
    pendingEventsRef.current = { gameId, requestId, events };
    setTraining((current) => ({ ...current, events }));
    client.send('UpdateTrainingEvents', { gameId, requestId, events });
  };

  const saveEventsFromEditor = (events: TrainingEventDefinition[]) => {
    saveEvents(events);
  };

  const publishModel = () => {
    if (!gameId || !adminUsername.trim() || !adminPassword) return;
    const requestId = crypto.randomUUID();
    publishRequestRef.current = requestId;
    setPublishRequestId(requestId);
    setPublishResult(null);
    client.send('PublishTrainingModel', {
      requestId,
      gameId,
      username: adminUsername.trim(),
      password: adminPassword,
    });
  };

  const findRegionPreviewSample = (events: TrainingEventDefinition[]) => {
    const objectClassIds = new Set(events
      .filter((event) => (event.detectionKind ?? 'Object') === 'Object')
      .map((event) => event.classId));
    const wantsOcr = events.some((event) => event.detectionKind === 'Ocr');
    const matches = training.samples.filter((sample) =>
      sample.labels.some((label) => objectClassIds.has(label.classId))
      || (wantsOcr && (sample.ocrRegions?.length ?? 0) > 0));
    return matches.find((sample) => samplePreviews[sample.id]) ?? matches[0];
  };

  const groupEvents = (group: TrainingRegionGroup) =>
    training.events.filter((event) => event.regionGroupId === group.id);

  const openRegionEditor = (
    target: TrainingEventDefinition | TrainingRegionGroup,
    targetType: 'event' | 'group',
    events: TrainingEventDefinition[],
  ) => {
    const sample = findRegionPreviewSample(events);
    if (sample && !samplePreviews[sample.id] && !previewRequestsRef.current.has(sample.id)) {
      requestPreview(sample);
    }
    setRegionEditor(targetType === 'group'
      ? { target: target as TrainingRegionGroup, targetType, sampleId: sample?.id }
      : { target: target as TrainingEventDefinition, targetType, sampleId: sample?.id });
  };

  const saveRegionGroups = (regionGroups: TrainingRegionGroup[]) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError('Tript is not connected.');
      return;
    }
    setEventError(null);
    const regionFields: Array<keyof TrainingRegionGroup> = [
      'screenRegionX', 'screenRegionY', 'screenRegionW', 'screenRegionH',
    ];
    const regionsChanged = regionGroups.length !== (training.regionGroups ?? []).length
      || regionGroups.some((group) => {
        const previous = (training.regionGroups ?? []).find((candidate) => candidate.id === group.id);
        return !previous || regionFields.some((field) => previous[field] !== group[field]);
      });
    if (training.model && regionsChanged) setInstalledRegionsStale(true);
    const requestId = `region-groups-${++editRequestCounterRef.current}`;
    latestRegionGroupsRequestRef.current = requestId;
    pendingRegionGroupsRef.current = { gameId, requestId, regionGroups };
    setTraining((current) => ({ ...current, regionGroups }));
    client.send('UpdateTrainingRegionGroups', { gameId, requestId, regionGroups });
  };

  const openRegion = (event: TrainingEventDefinition) => {
    const group = event.regionGroupId == null
      ? undefined
      : regionGroups.find((candidate) => candidate.id === event.regionGroupId);
    if (group) openRegionEditor(group, 'group', groupEvents(group));
    else openRegionEditor(event, 'event', [event]);
  };

  const openRegionGroup = (group: TrainingRegionGroup) => {
    openRegionEditor(group, 'group', groupEvents(group));
  };

  const saveRegion = (updated: TrainingEventDefinition | TrainingRegionGroup) => {
    if (regionEditor?.targetType === 'group') {
      saveRegionGroups(regionGroups.map((group) => group.id === updated.id ? updated as TrainingRegionGroup : group));
    } else {
      const currentEvents = pendingEventsRef.current?.events ?? training.events;
      saveEventsFromEditor(currentEvents.map((event) => event.id === updated.id ? updated as TrainingEventDefinition : event));
    }
    setRegionEditor(null);
  };

  const updateEventGroup = (eventId: number, value: string) => {
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    saveEvents(currentEvents.map((event) => event.id === eventId
      ? { ...event, regionGroupId: value ? Number(value) : null }
      : event));
  };

  const createRegionGroup = () => {
    const name = newGroupName.trim();
    if (!name) return;
    saveRegionGroups([...regionGroups, {
      id: Math.max(0, ...regionGroups.map((group) => group.id)) + 1,
      name,
      screenRegionX: null,
      screenRegionY: null,
      screenRegionW: null,
      screenRegionH: null,
    }]);
    setNewGroupName('');
  };

  const renameRegionGroup = (group: TrainingRegionGroup) => {
    const name = window.prompt('Region group name', group.name)?.trim();
    if (name && name !== group.name) {
      saveRegionGroups(regionGroups.map((candidate) => candidate.id === group.id ? { ...candidate, name } : candidate));
    }
  };

  const deleteRegionGroup = (group: TrainingRegionGroup) => {
    if (!window.confirm(`Delete the ${group.name} region group? Its events will be detached.`)) return;
    saveRegionGroups(regionGroups.filter((candidate) => candidate.id !== group.id));
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    saveEvents(currentEvents.map((event) => event.regionGroupId === group.id
      ? { ...event, regionGroupId: null }
      : event));
  };

  const loadSample = (sample: TrainingSample) => {
    if (gameId) {
      const requestId = `editor-${++previewRequestCounterRef.current}-${sample.id}`;
      selectedSampleRequestRef.current = requestId;
      client.send('GetTrainingSample', { gameId, sampleId: sample.id, requestId });
    }
  };

  const selectedSampleIndex = selectedSample
    ? filteredSamples.findIndex((sample) => sample.id === selectedSample.sample.id)
    : -1;
  const canNavigatePrevious = selectedSampleIndex > 0;
  const canNavigateNext = selectedSampleIndex >= 0 && selectedSampleIndex < filteredSamples.length - 1;
  const navigateSample = (direction: 'previous' | 'next') => {
    if (selectedSampleIndex < 0) return;
    const nextIndex = selectedSampleIndex + (direction === 'next' ? 1 : -1);
    const nextSample = filteredSamples[nextIndex];
    if (!nextSample) return;
    setSamplePage(Math.floor(nextIndex / SAMPLE_PAGE_SIZE) + 1);
    loadSample(nextSample);
  };

  return (
    <section className="training-view" aria-labelledby="training-title">
      <div className="training-header">
        <div className="training-heading-copy">
          <p className="training-eyebrow">Training workspace</p>
          <h1 id="training-title">Build a game-specific training set</h1>
          <p className="muted">Define the visual events, label frames from the player, and keep the model contract in one place.</p>
        </div>
        <div className="training-game-picker">
          <Field label="Game">
            <SelectField
              value={gameId}
              onChange={setGameId}
               options={[{ value: '', label: 'Choose a game' }, ...games.map((game) => ({ value: game.id, label: game.name }))]}
            />
          </Field>
        </div>
      </div>

      {!gameId && <p className="panel muted">No catalogue game is available for training.</p>}

      {gameId && (
        <>
          <div className="training-grid">
            <section className="panel training-panel">
              <div className="training-panel-heading">
                <div>
                  <p className="training-eyebrow">Contract</p>
                  <h2>Events and model</h2>
                </div>
                <span className="training-count">{training.events.length} classes</span>
                <Button variant="ghost" size="small" onClick={openNewEvent}>+ Add event</Button>
              </div>
              {eventError && <p className="training-event-error" role="alert">{eventError}</p>}
              <div className="training-model-facts">
                <span>Input</span>
                <strong>
                  {training.model?.inputWidth && training.model.inputHeight
                    ? `${training.model.inputWidth} × ${training.model.inputHeight}`
                    : 'No model'}
                </strong>
                <span>Runtime classes</span>
                <strong>{training.model?.classCount ?? 'Not trained'}</strong>
                <span>Editable samples</span>
                <strong>{training.samples.length}</strong>
                <span>Train images</span>
                <strong>{training.dataset?.trainingImages ?? 'Not exported'}</strong>
                <span>Validation images</span>
                <strong>{training.dataset?.validationImages ?? 'Not exported'}</strong>
              </div>
              {training.dataset?.warnings.map((warning) => (
                <p className="training-event-error" role="alert" key={warning}>{warning}</p>
              ))}
              {installedRegionsStale && (
                <p className="training-invalid-warning" role="status">
                  Region changes are saved for future training. The installed model still uses its previous regions; train and install again to apply them at runtime.
                </p>
              )}
              <div className="training-region-groups">
                <div className="training-palette-heading">
                  <div>
                    <p className="training-eyebrow">Shared crops</p>
                    <h3>Event folders</h3>
                  </div>
                </div>
                <div className="training-region-group-create">
                  <TextField
                    value={newGroupName}
                    onChange={setNewGroupName}
                    placeholder="New group name"
                    aria-label="New region group name"
                  />
                  <Button variant="ghost" size="small" onClick={createRegionGroup} disabled={!newGroupName.trim()}>Create group</Button>
                </div>
                <p className="muted small training-folder-help">Drag an event into a folder to share its region. Drop it into Ungrouped to use its own region.</p>
                <TrainingEventTree
                  events={training.events}
                  groups={regionGroups}
                  eventMeta={(event) => {
                    const coverage = eventCoverage.get(event.classId);
                    return <>{event.type}{coverage && ` · ${coverage.trainingSamples} train / ${coverage.validationSamples} validation frames`}</>;
                  }}
                  onEdit={(event) => setEventEditor({ event, isNew: false })}
                  onRegion={openRegion}
                  onDelete={(event) => {
                    if (window.confirm(`Delete the ${event.name} event?`)) deleteEvent(event.id);
                  }}
                  onMove={(eventId, groupId) => updateEventGroup(eventId, groupId == null ? '' : String(groupId))}
                  onRenameGroup={renameRegionGroup}
                  onRegionGroup={openRegionGroup}
                  onDeleteGroup={deleteRegionGroup}
                />
              </div>
            </section>

            <section className="panel training-panel training-import-panel">
              <p className="training-eyebrow">Workspace</p>
              <h2>Import an existing workspace</h2>
              <p className="muted">Use this when you already have an event contract, model, and full-resolution labeled samples on disk.</p>
              <div className="training-import-guide">
                <strong>Expected contents</strong>
                <span className="muted small"><code>events.json</code> and paired files in <code>samples/</code> are required. <code>model.onnx</code> is optional; <code>dataset/</code> is ignored.</span>
              </div>
              <Field label="Training folder" hint="The selected folder is copied into this game's local workspace.">
                <div className="training-path-row">
                  <TextField value={sourcePath} onChange={setSourcePath} placeholder={sourcePlaceholder} aria-label="Training folder" />
                  <Button variant="ghost" onClick={() => client.send('BrowseTrainingFolder')}>Browse</Button>
                </div>
              </Field>
              <div className="training-import-footer">
                 <span className="muted small">
                    {isImporting
                      ? 'Importing workspace...'
                      : folderPickerStatus === 'cancelled'
                      ? 'Folder selection cancelled'
                      : sourcePath.trim() ? 'Folder selected' : 'No folder selected'}
                  </span>
                <Button onClick={importAssets} disabled={!sourcePath.trim() || isImporting}>
                  {isImporting ? 'Importing...' : 'Import workspace'}
                </Button>
              </div>
            </section>
          </div>

          <section className="panel training-panel training-samples-panel">
            <div className="training-panel-heading">
              <div>
                <p className="training-eyebrow">Samples</p>
                <h2>Captured frames</h2>
              </div>
              <span className="training-count">{filteredSamples.length} of {training.samples.length} frames</span>
            </div>
            {training.samples.length === 0 ? (
              <div className="training-empty-state">
                <span className="training-empty-mark">01</span>
                <strong>No captured frames yet</strong>
                <p className="muted small">Open a recording, pause on the moment you want, then choose <strong>Label frame</strong>. The image and event palette open together.</p>
              </div>
            ) : (
              <>
                <div className="training-sample-toolbar">
                  <Field label="Filter samples">
                    <TextField
                      value={sampleFilter}
                      onChange={(value) => { setSampleFilter(value); setSamplePage(1); }}
                      placeholder="Search labels, IDs, or timestamps"
                      aria-label="Filter samples"
                    />
                  </Field>
                  <Field label="Validity">
                    <SelectField
                      value={sampleValidity}
                      onChange={(value) => { setSampleValidity(value as typeof sampleValidity); setSamplePage(1); }}
                      aria-label="Sample validity"
                      options={[
                        { value: 'all', label: 'All samples' },
                        { value: 'invalid', label: `Invalid (${invalidSamples.length})` },
                        { value: 'valid', label: `Valid (${training.samples.length - invalidSamples.length})` },
                      ]}
                    />
                  </Field>
                  <span className="muted small">Showing {pageSamples.length} samples</span>
                </div>
                {pageSamples.length === 0 ? (
                  <div className="training-empty-state">
                    <span className="training-empty-mark">--</span>
                    <strong>No samples match this filter</strong>
                    <p className="muted small">Try an event name, sample ID, or timestamp.</p>
                  </div>
                ) : (
                  <div className="training-sample-list">
                {pageSamples.map((sample) => (
                  <Button variant="ghost" className={`training-sample-card${invalidById.has(sample.id) ? ' invalid' : ''}`} key={sample.id} onClick={() => loadSample(sample)}>
                    {samplePreviews[sample.id] && !previewErrors[sample.id] ? (
                      <img
                        className="training-sample-thumb"
                        style={{ aspectRatio: `${sample.imageWidth} / ${sample.imageHeight}` }}
                        src={samplePreviews[sample.id]}
                        alt=""
                        onError={() => retryPreview(sample)}
                      />
                    ) : (
                      <span className="training-sample-thumb training-sample-thumb-empty">
                        {previewErrors[sample.id] ? 'Preview unavailable' : 'Loading preview'}
                      </span>
                    )}
                    <span className="training-sample-card-body">
                      <strong>{sample.id}</strong>
                      <small>{sample.labels.length} label{sample.labels.length === 1 ? '' : 's'} at {sample.timestampSeconds.toFixed(2)}s</small>
                      {invalidById.has(sample.id) && <span className="training-sample-invalid">Invalid: {invalidById.get(sample.id)}</span>}
                      <span className="training-label-chips">
                        {sample.labels.length === 0 ? (
                          <span className="training-label-chip muted">Unlabeled</span>
                        ) : sample.labels.map((label, index) => (
                          <span className="training-label-chip" key={`${sample.id}-${index}`}>
                            {training.events.find((event) => event.classId === label.classId)?.name ?? `Class ${label.classId}`}
                          </span>
                        ))}
                      </span>
                    </span>
                  </Button>
                ))}
                  </div>
                )}
                <div className="training-pagination" aria-label="Sample pages">
                  <Button variant="ghost" size="small" onClick={() => setSamplePage((page) => Math.max(1, page - 1))} disabled={samplePage <= 1}>Previous</Button>
                  <span className="muted small">Page {Math.min(samplePage, samplePageCount)} of {samplePageCount}</span>
                  <Button variant="ghost" size="small" onClick={() => setSamplePage((page) => Math.min(samplePageCount, page + 1))} disabled={samplePage >= samplePageCount}>Next</Button>
                </div>
              </>
            )}
          </section>

           <section className="panel training-panel training-controls">
             <div>
              <p className="training-eyebrow">Train</p>
              <h2>Build and activate</h2>
              <p className="muted">A model can only be trained from labeled frames. It is validated against the event contract before reload.</p>
              {invalidSamples.length > 0 && (
                <p className="training-invalid-warning" role="alert">
                  {invalidSamples.length} invalid sample{invalidSamples.length === 1 ? '' : 's'} will be skipped. {validLabeledSampleCount} valid labeled sample{validLabeledSampleCount === 1 ? '' : 's'} available.
                </p>
              )}
             </div>
              {trainingIsActive && (
                <div className="training-active-status" role="status">
                  <div className="training-active-heading">
                    <span className="training-active-dot" aria-hidden="true" />
                    <strong>Training in progress</strong>
                    {epochDetails && (
                      <span className="training-epoch-badge">Epoch {epochDetails.epoch}/{epochDetails.epochs}</span>
                    )}
                  </div>
                   <span className="muted small">
                    Requested device: {device === 'auto' ? 'Auto (actual device is shown in the console)' : device.toUpperCase()}
                    {' · '} {epochs} epochs {' · '} {training.model?.inputWidth ?? 640}x{training.model?.inputHeight ?? 640} input
                  </span>
                  {epochDetails && (
                    <div
                      className="training-epoch-progress"
                      role="progressbar"
                      aria-valuemin={0}
                      aria-valuemax={100}
                      aria-valuenow={Math.min(100, Math.round((epochDetails.epoch / epochDetails.epochs) * 100))}
                    >
                      <span style={{ width: `${Math.min(100, (epochDetails.epoch / epochDetails.epochs) * 100)}%` }} />
                    </div>
                  )}
                  {(hasLoss || hasMap50) && (
                    <div className="training-metrics">
                      <TrainingSparkline points={epochHistory} totalEpochs={epochDetails?.epochs ?? epochs} />
                      <span className="muted small training-metrics-legend">
                        {hasLoss && <span className="training-metrics-legend-loss">loss</span>}
                        {hasMap50 && <span className="training-metrics-legend-map">mAP50</span>}
                      </span>
                    </div>
                  )}
                  {epochDetails && (
                    <span className="muted small training-metrics-numbers">
                      {epochDetails.loss != null && <span>loss {epochDetails.loss.toFixed(4)}</span>}
                      {epochDetails.map50 != null && <span>mAP50 {epochDetails.map50.toFixed(4)}</span>}
                      {pace.elapsedMs > 0 && <span>{formatDuration(pace.elapsedMs)} elapsed</span>}
                      {pace.remainingMs != null && <span>~{formatDuration(pace.remainingMs)} remaining</span>}
                    </span>
                  )}
                  {statusNote && <span className="muted small">{statusNote}</span>}
                  <span className="muted small">Detailed Ultralytics output is open in the training console window.</span>
                </div>
              )}
            <div className="training-field compact">
              <Field label="Epochs">
                <TextField type="number" value={epochs} min={1} onChange={(value) => setEpochs(Number(value))} />
              </Field>
            </div>
            <div className="training-field compact">
              <Field label="Device">
                <SelectField
                  value={device}
                  onChange={setDevice}
                  options={[
                    { value: 'auto', label: 'Auto GPU / CPU' },
                    { value: 'rocm', label: 'ROCm / AMD GPU' },
                    { value: 'cuda', label: 'CUDA GPU' },
                    { value: 'directml', label: 'DirectML / AMD GPU' },
                    { value: 'cpu', label: 'CPU' },
                  ]}
                />
              </Field>
            </div>
            <div className="training-field compact">
              <Field label="Augmentation" hint="Adds mildly distorted copies of each training crop to grow small sample sets. Validation is never augmented.">
                <SelectField
                  value={String(augmentCopies)}
                  onChange={(value) => setAugmentCopies(Number(value))}
                  options={[
                    { value: '0', label: 'Off' },
                    { value: '2', label: '2 copies per crop' },
                    { value: '4', label: '4 copies per crop' },
                    { value: '8', label: '8 copies per crop' },
                  ]}
                />
              </Field>
            </div>
            {canPickScope && (
              <div className="training-field compact">
                <Field label="Retrain" hint="This game has object and OCR events. Retrain just one; the other model is kept as installed.">
                  <SelectField
                    value={trainingScope}
                    onChange={(value) => setTrainingScope(value as 'all' | 'object' | 'ocr')}
                    options={[
                      { value: 'all', label: 'Object + OCR' },
                      { value: 'object', label: 'Object detection only' },
                      { value: 'ocr', label: 'OCR recogniser only' },
                    ]}
                  />
                </Field>
              </div>
            )}
             <Button onClick={startTraining} disabled={trainingIsActive || validLabeledSampleCount === 0}>
               Start training
             </Button>
             <Button variant="ghost" onClick={() => client.send('CancelTraining')} disabled={!trainingIsActive}>
               Cancel
            </Button>
          </section>

          {/* While a run is active every message is mirrored into the in-progress panel above;
              this line only surfaces terminal states (completed/cancelled/error) and the
              non-training flows (import, event deletion). */}
          {progress && !trainingIsActive && (
            <p className={`training-progress training-progress-${progress.status}`} role="status">
              <strong>{progress.status}</strong> {progress.message}
            </p>
          )}

          <section className="training-panel training-publish">
            <div>
              <h2>Publish trained model</h2>
              <p className="muted small">Credentials are used for this upload only and are not saved.</p>
            </div>
            <div className="training-publish-fields">
              <Field label="Admin username">
                <TextField value={adminUsername} onChange={setAdminUsername} autoComplete="username" />
              </Field>
              <Field label="Admin password">
                <TextField type="password" value={adminPassword} onChange={setAdminPassword} autoComplete="current-password" />
              </Field>
              <Button onClick={publishModel} disabled={!training.model || !adminUsername.trim() || !adminPassword || publishRequestId !== null}>
                {publishRequestId ? 'Publishing…' : 'Publish model'}
              </Button>
            </div>
            {publishResult && <p className="training-progress" role="status">{publishResult}</p>}
          </section>

      {selectedSample && (
            <TrainingSampleEditor
              client={client}
              gameId={gameId}
              sample={selectedSample}
              events={training.events}
              regionGroups={regionGroups}
              hasModel={training.model != null}
              onEventsChange={saveEventsFromEditor}
              onRegionGroupsChange={saveRegionGroups}
              onNavigate={navigateSample}
              canNavigatePrevious={canNavigatePrevious}
              canNavigateNext={canNavigateNext}
              onClose={() => setSelectedSample(null)}
            />
          )}
        </>
      )}
      {eventEditor && (
        <TrainingEventEditor
          key={`${eventEditor.event.id}-${eventEditor.event.name}-${eventEditor.isNew}`}
          event={eventEditor.event}
          events={training.events}
          isNew={eventEditor.isNew}
          onCancel={() => setEventEditor(null)}
          onSave={saveEvent}
        />
      )}
      {regionEditor && (
        <TrainingRegionEditor
          target={regionEditor.target}
          targetType={regionEditor.targetType}
          backgroundImage={regionPreviewSample ? samplePreviews[regionPreviewSample.id] : undefined}
          imageWidth={regionPreviewSample?.imageWidth}
          imageHeight={regionPreviewSample?.imageHeight}
          onCancel={() => setRegionEditor(null)}
          onSave={saveRegion}
        />
      )}
      {deletingEventName && (
        <LoadingOverlay
          title={`Deleting ${deletingEventName} event`}
          description="Removing its labels from every training sample."
          progress={deletePercent}
        />
      )}
      {exportingDataset && (
        <LoadingOverlay
          title="Preparing training data"
          description="Cropping, splitting and augmenting the dataset. The workspace is locked until the dataset is ready; the console window shows details."
          cancelLabel="Cancel"
          onCancel={() => client.send('CancelTraining')}
        />
      )}
    </section>
  );
}
