// SPDX-License-Identifier: GPL-2.0-or-later
//
// The recording indicator, in the topbar. Its weight tracks what is actually happening: a live
// recording gets an elapsed clock and a stop control, an idle recorder gets a single button, and a
// dropped connection replaces the lot because nothing else on the bar can be acted on.
//
// Three things it deliberately does NOT show. A "Connected" badge, because connection is only
// information when it is broken. A game name when no game is detected — "Unknown game" is the app
// admitting it has nothing to say. And the word "Stopped", which the absent clock already says and
// the button already offers to change.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ConnectionState } from '../ipc/websocketClient';
import type {
  AvailableRecordingModel,
  AvailableRecordingModelsMessage,
  GameModelStatus,
  ModelStatusMessage,
  RecordingState,
  StateMessage,
  StartRecordingParameters,
} from '../ipc/protocol';
import { trainingEnabled } from '../buildFeatures';
import { useSettings } from '../settings/useSettings';
import {
  PRIMARY_DISPLAY_VALUE,
  buildDisplayOptions,
  displayFieldMode,
  displaySelectValue,
} from '../settings/displayModel';
import { Button, Icon, SelectField } from './ui/controls';
import { deriveRecorderState, formatElapsed } from './recorder/recorderState';

function ModelStatusIndicator({ status }: { status?: GameModelStatus }) {
  if (!status || status.stage === 'ready') {
    return null;
  }

  if (status.stage === 'error' || status.stage === 'unsupported') {
    const label = status.stage === 'error' ? 'Model error' : 'Model unsupported';
    return (
      <span className={`rec-model-status ${status.stage}`} role="status" data-testid="model-status">
        {status.message ? `${label}: ${status.message}` : label}
      </span>
    );
  }

  const labels = {
    checking: 'Checking model…',
    downloading: 'Downloading model…',
    verifying: 'Verifying model…',
    installing: 'Installing model…',
  } as const;
  const hasPercentage = status.stage === 'downloading'
    && typeof status.totalBytes === 'number'
    && status.totalBytes > 0;
  const percentage = hasPercentage
    ? Math.round(Math.min(1, Math.max(0, (status.completedBytes ?? 0) / status.totalBytes!)) * 100)
    : null;
  const text = percentage === null ? labels[status.stage] : `Model ${percentage}%`;
  const accessibleLabel = percentage === null
    ? labels[status.stage].replace('…', '')
    : `Downloading game model: ${percentage}%`;

  return (
    <span
      className={`rec-model-status active ${percentage === null ? 'indeterminate' : ''}`}
      role="progressbar"
      aria-label={accessibleLabel}
      aria-valuemin={percentage === null ? undefined : 0}
      aria-valuemax={percentage === null ? undefined : 100}
      aria-valuenow={percentage ?? undefined}
      data-testid="model-status"
    >
      <span className="rec-model-progress" aria-hidden="true">
        <span style={{ width: percentage === null ? '45%' : `${percentage}%` }} />
      </span>
      {text}
    </span>
  );
}

export function RecorderBar({
  client,
  connectionState,
  trainingFeatureEnabled = trainingEnabled,
  clipJobCount = 0,
  /** Injectable clock so the elapsed time is testable without fake timers. */
  nowSeconds,
}: {
  client: IpcClient;
  connectionState: ConnectionState;
  trainingFeatureEnabled?: boolean;
  clipJobCount?: number;
  nowSeconds?: number;
}) {
  const [recordingState, setRecordingState] = useState<RecordingState | null>(null);
  const [modelStatuses, setModelStatuses] = useState<GameModelStatus[]>([]);
  const [tick, setTick] = useState(() => Date.now() / 1000);
  const [pendingDisplay, setPendingDisplay] = useState<string | undefined>(undefined);
  const [availableModels, setAvailableModels] = useState<AvailableRecordingModel[]>([]);
  const { settings, availableDisplays } = useSettings(client);

  useEffect(() => {
    return client.on('state', (content) => {
      const message = content as StateMessage;
      const state = message?.state;
      if (state && typeof state === 'object' && 'recording' in state) {
        setRecordingState(state);
      }
    });
  }, [client]);

  useEffect(() => {
    return client.on('modelStatus', (content) => {
      const message = content as ModelStatusMessage;
      if (Array.isArray(message?.models)) {
        setModelStatuses(message.models);
      }
    });
  }, [client]);

  useEffect(() => {
    if (!trainingFeatureEnabled) {
      return;
    }
    return client.on('availableRecordingModels', (content) => {
      const message = content as AvailableRecordingModelsMessage;
      setAvailableModels(Array.isArray(message?.models) ? message.models : []);
    });
  }, [client, trainingFeatureEnabled]);

  const recording = recordingState?.recording ?? false;

  useEffect(() => {
    if (trainingFeatureEnabled && recording) {
      client.send('ListAvailableRecordingModels');
    }
  }, [client, recording, trainingFeatureEnabled]);

  const automaticClips = recordingState?.automaticClips;
  const creatingClips = clipJobCount > 0 || automaticClips?.active === true;
  const clipStatus = automaticClips?.active
    ? automaticClips.paused
      ? `Highlights paused (${automaticClips.completed}/${automaticClips.total})`
      : `Creating highlights (${automaticClips.completed}/${automaticClips.total})`
    : clipJobCount > 1
      ? `Creating ${clipJobCount} clips…`
      : 'Creating clips…';
  const activeModelGameId = recordingState?.activeModelGameId ?? null;
  const modelStatus = modelStatuses.find((status) =>
    status.gameId === (activeModelGameId ?? recordingState?.game?.id));
  const modelOptions = [
    { value: '', label: 'Load model…', disabled: true },
    ...availableModels.map((model) => ({ value: model.gameId, label: model.name })),
  ];
  const selectedModelId = activeModelGameId === null
    ? ''
    : availableModels.find((model) =>
        model.gameId.localeCompare(activeModelGameId, undefined, { sensitivity: 'accent' }) === 0)?.gameId ?? '';

  // One tick a second, and only while there is a clock to advance.
  useEffect(() => {
    if (!recording) {
      return;
    }
    const timer = setInterval(() => setTick(Date.now() / 1000), 1000);
    return () => clearInterval(timer);
  }, [recording]);

  const state = deriveRecorderState({
    connection: connectionState,
    recording,
    game: recordingState?.game?.name ?? recordingState?.game?.id ?? null,
    detected: recordingState?.game?.detected === true,
    startedAt: typeof recordingState?.startedAt === 'number' ? recordingState.startedAt : null,
  });

  const detected = recordingState?.game?.detected === true;
  const method = settings.capture.method;
  const displayCaptureActive = !detected && method !== 'Game';
  const showDisplaySelector = displayCaptureActive && displayFieldMode(availableDisplays) === 'picker';
  const recordDisabled = !detected && method === 'Game';
  const displaySelectorValue = pendingDisplay ?? displaySelectValue(settings.capture.display);
  const displayOptions = buildDisplayOptions(
    availableDisplays ?? [],
    settings.capture.display,
    settings.capture.displayLabel,
  );

  useEffect(() => {
    if (!showDisplaySelector) {
      setPendingDisplay(undefined);
    }
  }, [showDisplaySelector]);

  useEffect(() => {
    if (pendingDisplay === undefined || pendingDisplay === PRIMARY_DISPLAY_VALUE) {
      return;
    }
    if (!availableDisplays?.some((display) => display.id === pendingDisplay)) {
      setPendingDisplay(PRIMARY_DISPLAY_VALUE);
    }
  }, [availableDisplays, pendingDisplay]);

  if (state.kind === 'disconnected') {
    return (
      <div className="recorder-bar recorder-bar-error" data-testid="recorder-bar">
        <span className="rec-problem">⚠ Not connected</span>
      </div>
    );
  }

  if (state.kind === 'recording') {
    const now = nowSeconds ?? tick;
    return (
      <div className="recorder-bar" data-testid="recorder-bar">
        <span className="rec-dot recording" aria-hidden="true" />
        <span className="rec-elapsed" data-testid="recording-elapsed">
          {state.startedAt === null ? '--:--' : formatElapsed(now - state.startedAt)}
        </span>
        {state.game && <span className="rec-game">{state.game}</span>}
        {creatingClips && <span className="rec-activity" data-testid="clip-creation-status">{clipStatus}</span>}
        <ModelStatusIndicator status={modelStatus} />
        {trainingFeatureEnabled && availableModels.length > 0 && (
          <span className="rec-source" data-testid="recording-model-source">
            <span className="rec-source-label">Model</span>
            <SelectField
              compact
              className="rec-source-select"
              aria-label="Detection model"
              value={selectedModelId}
              options={modelOptions}
              onChange={(gameId) => {
                if (gameId) {
                  client.send('ActivateRecordingModel', { gameId });
                }
              }}
            />
          </span>
        )}
        <Button variant="ghost" size="small" onClick={() => client.send('StopRecording')}>
          Stop
        </Button>
      </div>
    );
  }

  return (
    <div className="recorder-bar" data-testid="recorder-bar">
      {creatingClips && <span className="rec-activity" data-testid="clip-creation-status">{clipStatus}</span>}
      {state.kind === 'detected' && (
        <>
          <span className="rec-dot detected" aria-hidden="true" />
          <span className="rec-game">Detected: {state.game}</span>
          <ModelStatusIndicator status={modelStatus} />
        </>
      )}
      {showDisplaySelector && (
        <span className="rec-source" data-testid="capture-source">
          <Icon name="monitor" size={16} />
          <SelectField
            compact
            className="rec-source-select"
            aria-label="Capture display"
            value={displaySelectorValue}
            options={displayOptions}
            onChange={(value) => setPendingDisplay(value)}
          />
        </span>
      )}
      <Button
        variant="ghost"
        size="small"
        disabled={recordDisabled}
        title={recordDisabled
          ? 'No game is detected. Set the capture method to Auto or Display to record the desktop.'
          : undefined}
        onClick={() => {
          const params: StartRecordingParameters = {};
          if (recordingState?.game?.detected && recordingState.game.id) {
            params.gameId = recordingState.game.id;
          }
          if (showDisplaySelector) {
            params.applyDisplay = true;
            params.displayId = displaySelectorValue === PRIMARY_DISPLAY_VALUE ? null : displaySelectorValue;
            setPendingDisplay(undefined);
          }
          client.send('StartRecording', Object.keys(params).length > 0 ? params : undefined);
        }}
      >
        Record
      </Button>
    </div>
  );
}
