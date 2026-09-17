// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import type {
  GameInfo,
  TrainingEventDefinition,
  TrainingMessage,
  TrainingProgressMessage,
  TrainingPushMessage,
  TrainingRegionGroup,
  TrainingSample,
  TrainingSampleMessage,
  TrainingUpdateResultMessage,
} from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { recordEpoch, type TrainingEpochPoint } from './trainingMetrics';
import { useSamplePreviews, type SamplePreviews } from './useSamplePreviews';
import type { TrainingOptions } from './TrainingControlsPanel';

const EMPTY_TRAINING: TrainingMessage = { gameId: null, events: [], samples: [], regionGroups: [], invalidSamples: [] };
const DEFAULT_EPOCHS = 100;
const DEFAULT_OCR_EPOCHS = 50;
const DEFAULT_DEVICE = 'auto';
const NOT_CONNECTED = 'Tript is not connected.';
const IMPORT_CONFIRMATION = 'This training workspace already contains data. Importing will replace events.json and the imported model, add full-resolution samples, and remove the current model if the import has no model. Prepared dataset images are ignored; existing captured samples are preserved. Continue?';

const EVENT_REGION_FIELDS: Array<keyof TrainingEventDefinition> = [
  'regionGroupId', 'screenRegionX', 'screenRegionY', 'screenRegionW', 'screenRegionH',
];
const GROUP_REGION_FIELDS: Array<keyof TrainingRegionGroup> = [
  'screenRegionX', 'screenRegionY', 'screenRegionW', 'screenRegionH',
];

type Preferences = Omit<TrainingOptions, 'scope'>;

const DEFAULT_PREFERENCES: Preferences = {
  epochs: DEFAULT_EPOCHS,
  device: DEFAULT_DEVICE,
  augmentCopies: 0,
  ocrEpochs: DEFAULT_OCR_EPOCHS,
};

export interface TrainingWorkspace {
  games: GameInfo[];
  gameId: string;
  setGameId: (gameId: string) => void;
  training: TrainingMessage;
  progress: TrainingProgressMessage | null;
  epochHistory: TrainingEpochPoint[];
  statusNote: string | null;
  preparingDataset: boolean;
  selectedSample: TrainingSampleMessage | null;
  closeSample: () => void;
  eventError: string | null;
  setEventError: (error: string | null) => void;
  deletingEventName: string | null;
  deletePercent: number | null;
  installedRegionsStale: boolean;
  isImporting: boolean;
  preferences: Preferences;
  setPreferences: (patch: Partial<Preferences>) => void;
  samplePage: number;
  setSamplePage: (page: number) => void;
  previews: SamplePreviews;
  currentEvents: () => TrainingEventDefinition[];
  importAssets: (sourcePath: string) => void;
  saveEvents: (events: TrainingEventDefinition[]) => void;
  saveRegionGroups: (regionGroups: TrainingRegionGroup[]) => void;
  deleteEvent: (eventId: number) => void;
  loadSample: (sample: TrainingSample) => void;
}

export function useTrainingWorkspace(client: IpcClient): TrainingWorkspace {
  const [games, setGames] = useState<GameInfo[]>([]);
  const [training, setTraining] = useState<TrainingMessage>(EMPTY_TRAINING);
  const [gameId, setGameId] = useState('');
  const [preferences, setPreferenceState] = useState<Preferences>(DEFAULT_PREFERENCES);
  const [progress, setProgress] = useState<TrainingProgressMessage | null>(null);
  const [epochHistory, setEpochHistory] = useState<TrainingEpochPoint[]>([]);
  const [statusNote, setStatusNote] = useState<string | null>(null);
  const [selectedSample, setSelectedSample] = useState<TrainingSampleMessage | null>(null);
  const [eventError, setEventError] = useState<string | null>(null);
  const [samplePage, setSamplePage] = useState(1);
  const [isImporting, setIsImporting] = useState(false);
  const [deletingEventName, setDeletingEventName] = useState<string | null>(null);
  const [deletePercent, setDeletePercent] = useState<number | null>(null);
  const [preparingDataset, setPreparingDataset] = useState(false);
  const [installedRegionsStale, setInstalledRegionsStale] = useState(false);
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
  const selectedSampleRequestRef = useRef<string | null>(null);
  activeGameIdRef.current = gameId;
  const previews = useSamplePreviews(client, gameId);
  const resetPreviews = previews.reset;

  const setPreferences = useCallback((patch: Partial<Preferences>) => {
    setPreferenceState((current) => ({ ...current, ...patch }));
  }, []);

  useEffect(() => {
    const listTraining = () => {
      if (activeGameIdRef.current) client.send('ListTraining', { gameId: activeGameIdRef.current });
    };
    const removeGames = client.on('gameList', (content) => {
      const nextGames = Array.isArray(content) ? (content as GameInfo[]) : [];
      setGames(nextGames);
      setGameId((current) => current || nextGames[0]?.id || '');
    });
    const removeTraining = client.on('training', (content) => {
      const push = content as Partial<TrainingPushMessage>;
      const message = push.training;
      if (!message) return;
      if (message.gameId && message.gameId !== activeGameIdRef.current) return;
      if (push.updateKind === 'events' && push.requestId !== latestEventsRequestRef.current) return;
      if (push.updateKind === 'regionGroups' && push.requestId !== latestRegionGroupsRequestRef.current) return;
      if (push.updateKind === 'events') pendingEventsRef.current = null;
      if (push.updateKind === 'regionGroups') pendingRegionGroupsRef.current = null;
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
          setPreferenceState({
            epochs: message.preferences?.epochs ?? DEFAULT_EPOCHS,
            device: message.preferences?.device ?? DEFAULT_DEVICE,
            augmentCopies: message.preferences?.augmentCopies ?? 0,
            ocrEpochs: message.preferences?.ocrEpochs ?? DEFAULT_OCR_EPOCHS,
          });
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
        setEpochHistory((current) => recordEpoch(current, {
          epoch: details.epoch,
          loss: details.loss,
          map50: details.map50,
          receivedAt: Date.now(),
        }));
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
        resetPreviews();
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
        listTraining();
      }
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
      listTraining();
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
      listTraining();
    });
    const removeSample = client.on('trainingSample', (content) => {
      const message = content as TrainingSampleMessage;
      if (message.requestId && message.requestId !== selectedSampleRequestRef.current) return;
      if (message.gameId === activeGameIdRef.current) setSelectedSample(message);
    });
    const removeConnection = client.onStateChange((state) => {
      if (state === 'connected') return;
      setDeletingEventName(null);
      setDeletePercent(null);
      setPreparingDataset(false);
    });
    client.send('ListGames');
    return () => {
      removeGames();
      removeTraining();
      removeProgress();
      removeError();
      removeEventsResult();
      removeRegionGroupsResult();
      removeSample();
      removeConnection();
    };
  }, [client, resetPreviews]);

  useEffect(() => {
    if (!gameId) return;
    loadedTrainingGameIdRef.current = null;
    client.send('ListTraining', { gameId });
    setSelectedSample(null);
    setProgress(null);
    setPreparingDataset(false);
    setEpochHistory([]);
    setStatusNote(null);
    setInstalledRegionsStale(false);
    resetPreviews();
    selectedSampleRequestRef.current = null;
    pendingEventsRef.current = null;
    pendingRegionGroupsRef.current = null;
    latestEventsRequestRef.current = null;
    latestRegionGroupsRequestRef.current = null;
    setSamplePage(1);
  }, [client, gameId, resetPreviews]);

  const currentEvents = () => pendingEventsRef.current?.events ?? training.events;

  const importAssets = (sourcePath: string) => {
    if (!gameId || !sourcePath || isImporting || loadedTrainingGameIdRef.current !== gameId) return;
    const hasExistingData = training.events.length > 0 || training.samples.length > 0;
    if (hasExistingData && !window.confirm(IMPORT_CONFIRMATION)) return;

    importingRef.current = true;
    setIsImporting(true);
    client.send('ImportTrainingAssets', {
      gameId,
      sourcePath,
      confirmOverwrite: true,
      expectedRevision: training.revision,
    });
  };

  const saveEvents = (events: TrainingEventDefinition[]) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError(NOT_CONNECTED);
      return;
    }
    setEventError(null);
    const regionsChanged = events.length !== training.events.length || events.some((event) => {
      const previous = training.events.find((candidate) => candidate.id === event.id);
      return !previous || EVENT_REGION_FIELDS.some((field) => previous[field] !== event[field]);
    });
    if (training.model && regionsChanged) setInstalledRegionsStale(true);
    const requestId = `events-${++editRequestCounterRef.current}`;
    latestEventsRequestRef.current = requestId;
    pendingEventsRef.current = { gameId, requestId, events };
    setTraining((current) => ({ ...current, events }));
    client.send('UpdateTrainingEvents', { gameId, requestId, events });
  };

  const saveRegionGroups = (regionGroups: TrainingRegionGroup[]) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError(NOT_CONNECTED);
      return;
    }
    setEventError(null);
    const previousGroups = training.regionGroups ?? [];
    const regionsChanged = regionGroups.length !== previousGroups.length
      || regionGroups.some((group) => {
        const previous = previousGroups.find((candidate) => candidate.id === group.id);
        return !previous || GROUP_REGION_FIELDS.some((field) => previous[field] !== group[field]);
      });
    if (training.model && regionsChanged) setInstalledRegionsStale(true);
    const requestId = `region-groups-${++editRequestCounterRef.current}`;
    latestRegionGroupsRequestRef.current = requestId;
    pendingRegionGroupsRef.current = { gameId, requestId, regionGroups };
    setTraining((current) => ({ ...current, regionGroups }));
    client.send('UpdateTrainingRegionGroups', { gameId, requestId, regionGroups });
  };

  const deleteEvent = (eventId: number) => {
    if (!gameId) return;
    if (client.state !== 'connected') {
      setEventError(NOT_CONNECTED);
      return;
    }
    const events = currentEvents();
    if (events.length <= 1) {
      setEventError('A training workspace must keep at least one event.');
      return;
    }
    const event = events.find((candidate) => candidate.id === eventId);
    setDeletingEventName(event?.name ?? 'event');
    setDeletePercent(0);
    saveEvents(events.filter((candidate) => candidate.id !== eventId));
  };

  const loadSample = (sample: TrainingSample) => {
    if (!gameId) return;
    const requestId = `editor-${previews.nextRequestNumber()}-${sample.id}`;
    selectedSampleRequestRef.current = requestId;
    client.send('GetTrainingSample', { gameId, sampleId: sample.id, requestId });
  };

  return {
    games,
    gameId,
    setGameId,
    training,
    progress,
    epochHistory,
    statusNote,
    preparingDataset,
    selectedSample,
    closeSample: () => setSelectedSample(null),
    eventError,
    setEventError,
    deletingEventName,
    deletePercent,
    installedRegionsStale,
    isImporting,
    preferences,
    setPreferences,
    samplePage,
    setSamplePage,
    previews,
    currentEvents,
    importAssets,
    saveEvents,
    saveRegionGroups,
    deleteEvent,
    loadSample,
  };
}
