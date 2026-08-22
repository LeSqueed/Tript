import { useEffect, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import { Button, Field, SelectField, TextField } from './ui/controls';
import { TrainingSampleEditor } from './TrainingSampleEditor';
import { TrainingEventEditor } from './TrainingEventEditor';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import type {
  GameInfo,
  TrainingEventDefinition,
  TrainingMessage,
  TrainingProgressMessage,
  TrainingSample,
  TrainingSampleMessage,
  TrainingSamplePreviewMessage,
} from '../ipc/protocol';

interface TrainingViewProps {
  client: IpcClient;
}

const EMPTY_TRAINING: TrainingMessage = { gameId: null, events: [], samples: [] };
const SAMPLE_PAGE_SIZE = 8;

export function TrainingView({ client }: TrainingViewProps) {
  const [games, setGames] = useState<GameInfo[]>([]);
  const [training, setTraining] = useState<TrainingMessage>(EMPTY_TRAINING);
  const [gameId, setGameId] = useState('');
  const [sourcePath, setSourcePath] = useState('');
  const [folderPickerStatus, setFolderPickerStatus] = useState<'idle' | 'selected' | 'cancelled'>('idle');
  const [epochs, setEpochs] = useState(100);
  const [device, setDevice] = useState('auto');
  const [progress, setProgress] = useState<TrainingProgressMessage | null>(null);
  const [selectedSample, setSelectedSample] = useState<TrainingSampleMessage | null>(null);
  const [eventEditor, setEventEditor] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionEditor, setRegionEditor] = useState<TrainingEventDefinition | null>(null);
  const [eventError, setEventError] = useState<string | null>(null);
  const [samplePreviews, setSamplePreviews] = useState<Record<string, string>>({});
  const [sampleFilter, setSampleFilter] = useState('');
  const [samplePage, setSamplePage] = useState(1);
  const [isImporting, setIsImporting] = useState(false);
  const pendingEventsRef = useRef<{ gameId: string; events: TrainingEventDefinition[] } | null>(null);
  const importingRef = useRef(false);
  const loadedTrainingGameIdRef = useRef<string | null>(null);
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
        const message = (content as { training?: TrainingMessage }).training;
        if (message) {
          if (message.gameId && gameId && message.gameId !== gameId) return;
          if (pendingEventsRef.current?.gameId === message.gameId) return;
          loadedTrainingGameIdRef.current = message.gameId;
          setTraining(message);
          if (message.gameId) setGameId(message.gameId);
        }
      });
      const removeProgress = client.on('trainingProgress', (content) => {
        const message = content as TrainingProgressMessage;
        if (message.gameId !== gameId) return;
        setProgress(message);
      if (message.status === 'eventsUpdated') pendingEventsRef.current = null;
      if (message.status === 'imported') {
        importingRef.current = false;
        setIsImporting(false);
        setSelectedSample(null);
        setSamplePreviews({});
        setSamplePage(1);
        client.send('ListTraining', { gameId: message.gameId });
      }
    });
    const removeError = client.on('error', () => {
      if (importingRef.current) {
        importingRef.current = false;
        setIsImporting(false);
        if (gameId) client.send('ListTraining', { gameId });
      }
      if (pendingEventsRef.current) {
        pendingEventsRef.current = null;
        if (gameId) client.send('ListTraining', { gameId });
      }
    });
      const removeSample = client.on('trainingSample', (content) => {
      const message = content as TrainingSampleMessage;
      if (message.gameId === gameId) setSelectedSample(message);
    });
    const removePreview = client.on('trainingSamplePreview', (content) => {
      const preview = content as TrainingSamplePreviewMessage;
      if (preview.gameId === gameId)
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
    // The initial gameList push can happen before this route mounts. Request it again so the
    // training picker is populated when the user navigates here later.
    client.send('ListGames');
    return () => {
      removeGames();
      removeTraining();
      removeProgress();
      removeError();
      removeSample();
      removePreview();
      removeFolder();
      removeFolderCancelled();
    };
  }, [client, gameId]);

  useEffect(() => {
    if (gameId) {
      loadedTrainingGameIdRef.current = null;
      client.send('ListTraining', { gameId });
      setSelectedSample(null);
      setSamplePreviews({});
      setSamplePage(1);
    }
  }, [client, gameId]);

  const importAssets = () => {
    if (!gameId || !sourcePath.trim() || isImporting || loadedTrainingGameIdRef.current !== gameId) return;
    const hasExistingData = training.events.length > 0 || training.samples.length > 0 || totalDatasetImages > 0;
    if (hasExistingData && !window.confirm(
      'This training workspace already contains data. Importing will replace events.json and the imported model, overwrite matching dataset files, and remove the current model if the import has no model. Existing captured samples will be preserved; imported dataset images will be added to the sample gallery. Continue?',
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

  const dataset = training.dataset ?? { trainingImages: 0, validationImages: 0 };
  const regionPreviewSample = training.samples.find((sample) => samplePreviews[sample.id]);
  const totalDatasetImages = dataset.trainingImages + dataset.validationImages;
  const hasUnlabeledSamples = training.samples.some((sample) => sample.labels.length === 0);
  const normalizedSampleFilter = sampleFilter.trim().toLowerCase();
  const filteredSamples = training.samples.filter((sample) => {
    if (!normalizedSampleFilter) return true;
    const labels = sample.labels.map((label) => training.events.find((event) => event.classId === label.classId)?.name ?? String(label.classId));
    return [sample.id, sample.timestampSeconds.toFixed(2), ...labels]
      .some((value) => value.toLowerCase().includes(normalizedSampleFilter));
  });
  const samplePageCount = Math.max(1, Math.ceil(filteredSamples.length / SAMPLE_PAGE_SIZE));
  const pageSamples = filteredSamples.slice((samplePage - 1) * SAMPLE_PAGE_SIZE, samplePage * SAMPLE_PAGE_SIZE);
  const pageSampleIds = pageSamples.map((sample) => sample.id).join('|');

  useEffect(() => {
    if (!gameId || !pageSampleIds) return;
    pageSamples.forEach((sample) => {
      if (!samplePreviews[sample.id]) {
        client.send('GetTrainingSample', { gameId, sampleId: sample.id, previewOnly: true });
      }
    });
  }, [client, gameId, pageSampleIds, samplePage, samplePreviews]);

  useEffect(() => {
    if (samplePage > samplePageCount) setSamplePage(samplePageCount);
  }, [samplePage, samplePageCount]);

  const startTraining = () => {
    if (gameId) {
      client.send('StartTraining', { gameId, epochs, device });
    }
  };

  const openNewEvent = () => {
    const nextId = Math.max(0, ...training.events.map((event) => event.id)) + 1;
    const nextClassId = Math.max(-1, ...training.events.map((event) => event.classId)) + 1;
    setEventEditor({
      event: {
        id: nextId,
        classId: nextClassId,
        name: 'New event',
        type: 'Trigger',
        bookmarkType: 'Manual',
        lifetimeMs: null,
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
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    if (currentEvents.length <= 1) {
      setEventError('A training workspace must keep at least one event.');
      return;
    }
    saveEvents(currentEvents.filter((event) => event.id !== eventId));
  };

  const saveEvents = (events: TrainingEventDefinition[]) => {
    if (!gameId) return;
    pendingEventsRef.current = { gameId, events };
    setTraining((current) => ({ ...current, events }));
    client.send('UpdateTrainingEvents', { gameId, events });
  };

  const saveEventsFromEditor = (events: TrainingEventDefinition[]) => {
    saveEvents(events);
  };

  const saveRegion = (updatedEvent: TrainingEventDefinition) => {
    const currentEvents = pendingEventsRef.current?.events ?? training.events;
    saveEventsFromEditor(currentEvents.map((event) => event.id === updatedEvent.id ? updatedEvent : event));
    setRegionEditor(null);
  };

  const loadSample = (sample: TrainingSample) => {
    if (gameId) {
      client.send('GetTrainingSample', { gameId, sampleId: sample.id });
    }
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
                <span>Imported dataset</span>
                <strong className={totalDatasetImages === 0 ? 'training-dataset-empty' : undefined}>
                  {totalDatasetImages === 0
                    ? 'No images yet'
                    : `${dataset.trainingImages} train · ${dataset.validationImages} validation`}
                </strong>
              </div>
              <ul className="training-event-list">
                {training.events.map((event) => (
                  <li key={event.classId}>
                    <span className="training-class-id">{event.classId}</span>
                    <span>{event.name}</span>
                    <span className="training-event-actions">
                      <small>{event.type}</small>
                      <Button variant="ghost" size="small" onClick={() => setEventEditor({ event, isNew: false })}>Edit</Button>
                      <Button variant="ghost" size="small" onClick={() => setRegionEditor(event)}>Region</Button>
                      <Button variant="ghost" size="small" onClick={() => {
                        if (window.confirm(`Delete the ${event.name} event?`)) deleteEvent(event.id);
                      }}>Delete</Button>
                    </span>
                  </li>
                ))}
              </ul>
            </section>

            <section className="panel training-panel training-import-panel">
              <p className="training-eyebrow">Workspace</p>
              <h2>Import an existing workspace</h2>
              <p className="muted">Use this when you already have an event contract, model, or prepared dataset on disk.</p>
              <div className="training-import-guide">
                <strong>Expected contents</strong>
                <span className="muted small"><code>events.json</code> is required. <code>model.onnx</code> and <code>dataset/</code> are optional.</span>
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
                  <Button variant="ghost" className="training-sample-card" key={sample.id} onClick={() => loadSample(sample)}>
                    {samplePreviews[sample.id] ? (
                      <img className="training-sample-thumb" src={samplePreviews[sample.id]} alt="" />
                    ) : (
                      <span className="training-sample-thumb training-sample-thumb-empty">Loading preview</span>
                    )}
                    <span className="training-sample-card-body">
                      <strong>{sample.id}</strong>
                      <small>{sample.labels.length} label{sample.labels.length === 1 ? '' : 's'} at {sample.timestampSeconds.toFixed(2)}s</small>
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
            </div>
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
                    { value: 'cuda', label: 'CUDA GPU' },
                    { value: 'cpu', label: 'CPU' },
                  ]}
                />
              </Field>
            </div>
            <Button onClick={startTraining} disabled={hasUnlabeledSamples || (training.samples.length === 0 && !training.model)}>
              Start training
            </Button>
            <Button variant="ghost" onClick={() => client.send('CancelTraining')}>
              Cancel
            </Button>
          </section>

          {progress && (
            <p className={`training-progress training-progress-${progress.status}`} role="status">
              <strong>{progress.status}</strong> {progress.message}
            </p>
          )}

      {selectedSample && (
            <TrainingSampleEditor
              client={client}
              gameId={gameId}
              sample={selectedSample}
              events={training.events}
              onEventsChange={saveEventsFromEditor}
              onClose={() => setSelectedSample(null)}
            />
          )}
        </>
      )}
      {eventEditor && (
        <TrainingEventEditor
          key={`${eventEditor.event.id}-${eventEditor.event.name}-${eventEditor.isNew}`}
          event={eventEditor.event}
          isNew={eventEditor.isNew}
          onCancel={() => setEventEditor(null)}
          onSave={saveEvent}
        />
      )}
      {regionEditor && (
        <TrainingRegionEditor
          event={regionEditor}
          events={training.events}
          backgroundImage={regionPreviewSample ? samplePreviews[regionPreviewSample.id] : undefined}
          imageWidth={regionPreviewSample?.imageWidth}
          imageHeight={regionPreviewSample?.imageHeight}
          onCancel={() => setRegionEditor(null)}
          onSave={saveRegion}
        />
      )}
    </section>
  );
}
