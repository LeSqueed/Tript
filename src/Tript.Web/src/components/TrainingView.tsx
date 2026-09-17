// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import { Button, Field, SelectField, TextField } from './ui/controls';
import { LoadingOverlay } from './ui/LoadingOverlay';
import { TrainingSampleEditor } from './TrainingSampleEditor';
import { TrainingEventEditor } from './TrainingEventEditor';
import { TrainingEventTree } from './TrainingEventTree';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import type { TrainingEventDefinition, TrainingRegionGroup } from '../ipc/protocol';
import { filterTrainingSamples, type SampleKindFilter } from './training/trainingMetrics';
import { TrainingControlsPanel, type TrainingScope } from './training/TrainingControlsPanel';
import { TrainingImportPanel } from './training/TrainingImportPanel';
import { TrainingPublishForm } from './training/TrainingPublishForm';
import { TrainingSampleList } from './training/TrainingSampleList';
import { useTrainingWorkspace } from './training/useTrainingWorkspace';

interface TrainingViewProps {
  client: IpcClient;
}

const SAMPLE_PAGE_SIZE = 8;
const TERMINAL_STATUS_LABELS: Record<string, string> = {
  completed: 'Done', cancelled: 'Cancelled', error: 'Failed', imported: 'Imported',
};

type RegionEditorState =
  | { target: TrainingEventDefinition; targetType: 'event'; sampleId?: string }
  | { target: TrainingRegionGroup; targetType: 'group'; sampleId?: string };

const isObjectEvent = (event: TrainingEventDefinition) => (event.detectionKind ?? 'Object') === 'Object';

export function TrainingView({ client }: TrainingViewProps) {
  const workspace = useTrainingWorkspace(client);
  const {
    games,
    gameId,
    training,
    progress,
    previews,
    samplePage,
    setSamplePage,
    currentEvents,
    saveEvents,
    saveRegionGroups,
    loadSample,
  } = workspace;
  const [trainingScope, setTrainingScope] = useState<TrainingScope>('all');
  const [eventEditor, setEventEditor] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionEditor, setRegionEditor] = useState<RegionEditorState | null>(null);
  const [sampleFilter, setSampleFilter] = useState('');
  const [sampleKind, setSampleKind] = useState<SampleKindFilter>('all');
  const [newGroupName, setNewGroupName] = useState('');

  const regionGroups = training.regionGroups ?? [];
  const invalidSamples = training.invalidSamples ?? [];
  const invalidById = new Map(invalidSamples.map((sample) => [sample.id, sample.reason]));
  const validLabeledSampleCount = training.samples.filter((sample) =>
    (sample.labels.length > 0 || (sample.ocrRegions?.length ?? 0) > 0)
      && !invalidById.has(sample.id)).length;
  const hasObjectEvents = training.events.some(isObjectEvent);
  const hasOcrEvents = training.events.some((event) => event.detectionKind === 'Ocr');
  const canPickScope = hasObjectEvents && hasOcrEvents;
  const effectiveScope = canPickScope ? trainingScope : 'all';
  const willTrainObject = hasObjectEvents && effectiveScope !== 'ocr';
  const willTrainOcr = hasOcrEvents && effectiveScope !== 'object';
  const trainingIsActive = training.trainingActive
    || progress?.status === 'exporting' || progress?.status === 'progress';
  const exportingDataset = workspace.preparingDataset || training.trainingPhase === 'exporting';
  const epochDetails = progress?.details && progress.details.epoch > 0 ? progress.details : null;
  const ocrSampleCount = training.samples.filter((sample) => (sample.ocrRegions?.length ?? 0) > 0).length;
  const objectSampleCount = training.samples.filter((sample) => sample.labels.length > 0).length;
  const filteredSamples = filterTrainingSamples(training.samples, training.events, invalidById, sampleKind, sampleFilter);
  const samplePageCount = Math.max(1, Math.ceil(filteredSamples.length / SAMPLE_PAGE_SIZE));
  const pageSamples = filteredSamples.slice((samplePage - 1) * SAMPLE_PAGE_SIZE, samplePage * SAMPLE_PAGE_SIZE);
  const pageSampleIds = pageSamples.map((sample) => sample.id).join('|');
  const eventCoverage = new Map(
    training.dataset?.eventCoverage.map((coverage) => [coverage.classId, coverage]) ?? [],
  );
  const regionPreviewSample = regionEditor?.sampleId
    ? training.samples.find((sample) => sample.id === regionEditor.sampleId)
    : undefined;

  useEffect(() => {
    if (!gameId || !pageSampleIds) return;
    previews.requestMissing(pageSamples);
  }, [client, gameId, pageSampleIds, samplePage, previews.images]);

  useEffect(() => {
    if (samplePage > samplePageCount) setSamplePage(samplePageCount);
  }, [samplePage, samplePageCount, setSamplePage]);

  const startTraining = () => {
    if (gameId) {
      client.send('StartTraining', {
        gameId,
        ...workspace.preferences,
        scope: canPickScope ? trainingScope : 'all',
      });
    }
  };

  const openNewEvent = () => {
    const nextId = Math.max(0, ...training.events.map((event) => event.id)) + 1;
    const nextClassId = Math.max(-1, ...training.events.filter(isObjectEvent).map((event) => event.classId)) + 1;
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
    const events = currentEvents();
    saveEvents(events.some((event) => event.id === nextEvent.id)
      ? events.map((event) => event.id === nextEvent.id ? nextEvent : event)
      : [...events, nextEvent]);
    workspace.setEventError(null);
    setEventEditor(null);
  };

  const findRegionPreviewSample = (events: TrainingEventDefinition[]) => {
    const objectClassIds = new Set(events.filter(isObjectEvent).map((event) => event.classId));
    const wantsOcr = events.some((event) => event.detectionKind === 'Ocr');
    const matches = training.samples.filter((sample) =>
      sample.labels.some((label) => objectClassIds.has(label.classId))
      || (wantsOcr && (sample.ocrRegions?.length ?? 0) > 0));
    return matches.find((sample) => previews.images[sample.id]) ?? matches[0];
  };

  const groupEvents = (group: TrainingRegionGroup) =>
    training.events.filter((event) => event.regionGroupId === group.id);

  const openRegionEditor = (
    target: TrainingEventDefinition | TrainingRegionGroup,
    targetType: 'event' | 'group',
    events: TrainingEventDefinition[],
  ) => {
    const sample = findRegionPreviewSample(events);
    if (sample) previews.requestMissing([sample]);
    setRegionEditor(targetType === 'group'
      ? { target: target as TrainingRegionGroup, targetType, sampleId: sample?.id }
      : { target: target as TrainingEventDefinition, targetType, sampleId: sample?.id });
  };

  const openRegion = (event: TrainingEventDefinition) => {
    const group = event.regionGroupId == null
      ? undefined
      : regionGroups.find((candidate) => candidate.id === event.regionGroupId);
    if (group) openRegionEditor(group, 'group', groupEvents(group));
    else openRegionEditor(event, 'event', [event]);
  };

  const saveRegion = (updated: TrainingEventDefinition | TrainingRegionGroup) => {
    if (regionEditor?.targetType === 'group') {
      saveRegionGroups(regionGroups.map((group) => group.id === updated.id ? updated as TrainingRegionGroup : group));
    } else {
      saveEvents(currentEvents().map((event) => event.id === updated.id ? updated as TrainingEventDefinition : event));
    }
    setRegionEditor(null);
  };

  const moveEventToGroup = (eventId: number, groupId: number | null) => {
    saveEvents(currentEvents().map((event) => event.id === eventId
      ? { ...event, regionGroupId: groupId }
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
    saveEvents(currentEvents().map((event) => event.regionGroupId === group.id
      ? { ...event, regionGroupId: null }
      : event));
  };

  const selectedSampleIndex = workspace.selectedSample
    ? filteredSamples.findIndex((sample) => sample.id === workspace.selectedSample?.sample.id)
    : -1;
  const navigateSample = (direction: 'previous' | 'next') => {
    if (selectedSampleIndex < 0) return;
    const nextIndex = selectedSampleIndex + (direction === 'next' ? 1 : -1);
    const nextSample = filteredSamples[nextIndex];
    if (!nextSample) return;
    setSamplePage(Math.floor(nextIndex / SAMPLE_PAGE_SIZE) + 1);
    loadSample(nextSample);
  };

  const kindOptions: { value: SampleKindFilter; label: string }[] = [
    { value: 'all', label: 'All samples' },
    { value: 'invalid', label: `Invalid (${invalidSamples.length})` },
    { value: 'valid', label: `Valid (${training.samples.length - invalidSamples.length})` },
    ...(hasOcrEvents ? [{ value: 'ocr' as const, label: `OCR regions (${ocrSampleCount})` }] : []),
    ...(hasOcrEvents && hasObjectEvents
      ? [{ value: 'object' as const, label: `Object labels (${objectSampleCount})` }] : []),
  ];

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
              onChange={workspace.setGameId}
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
              {workspace.eventError && <p className="training-event-error" role="alert">{workspace.eventError}</p>}
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
              {workspace.installedRegionsStale && (
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
                    if (window.confirm(`Delete the ${event.name} event?`)) workspace.deleteEvent(event.id);
                  }}
                  onMove={moveEventToGroup}
                  onRenameGroup={renameRegionGroup}
                  onRegionGroup={(group) => openRegionEditor(group, 'group', groupEvents(group))}
                  onDeleteGroup={deleteRegionGroup}
                />
              </div>
            </section>

            <TrainingImportPanel client={client} importing={workspace.isImporting} onImport={workspace.importAssets} />
          </div>

          <TrainingSampleList
            totalCount={training.samples.length}
            filteredCount={filteredSamples.length}
            pageSamples={pageSamples}
            events={training.events}
            invalidById={invalidById}
            previews={previews}
            query={sampleFilter}
            onQueryChange={(value) => { setSampleFilter(value); setSamplePage(1); }}
            kind={sampleKind}
            onKindChange={(value) => { setSampleKind(value); setSamplePage(1); }}
            kindOptions={kindOptions}
            page={samplePage}
            pageCount={samplePageCount}
            onPageChange={setSamplePage}
            onOpen={loadSample}
          />

          <TrainingControlsPanel
            options={{ ...workspace.preferences, scope: trainingScope }}
            onOptionsChange={({ scope, ...preferences }) => {
              if (scope) setTrainingScope(scope);
              workspace.setPreferences(preferences);
            }}
            hasObjectEvents={hasObjectEvents}
            hasOcrEvents={hasOcrEvents}
            invalidCount={invalidSamples.length}
            validLabeledCount={validLabeledSampleCount}
            active={trainingIsActive}
            epochDetails={epochDetails}
            epochHistory={workspace.epochHistory}
            statusNote={workspace.statusNote}
            modelSize={{ width: training.model?.inputWidth, height: training.model?.inputHeight }}
            onStart={startTraining}
            onCancel={() => client.send('CancelTraining')}
          />

          {progress && !trainingIsActive && (
            <p className={`training-progress training-progress-${progress.status}`} role="status">
              <strong>{TERMINAL_STATUS_LABELS[progress.status] ?? progress.status}</strong>{' '}
              {progress.message}
            </p>
          )}

          <TrainingPublishForm client={client} gameId={gameId} hasModel={Boolean(training.model)} />

          {workspace.selectedSample && (
            <TrainingSampleEditor
              client={client}
              gameId={gameId}
              sample={workspace.selectedSample}
              events={training.events}
              regionGroups={regionGroups}
              hasModel={training.model != null}
              onEventsChange={saveEvents}
              onRegionGroupsChange={saveRegionGroups}
              onNavigate={navigateSample}
              canNavigatePrevious={selectedSampleIndex > 0}
              canNavigateNext={selectedSampleIndex >= 0 && selectedSampleIndex < filteredSamples.length - 1}
              onClose={workspace.closeSample}
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
          backgroundImage={regionPreviewSample ? previews.images[regionPreviewSample.id] : undefined}
          imageWidth={regionPreviewSample?.imageWidth}
          imageHeight={regionPreviewSample?.imageHeight}
          onCancel={() => setRegionEditor(null)}
          onSave={saveRegion}
        />
      )}
      {workspace.deletingEventName && (
        <LoadingOverlay
          title={`Deleting ${workspace.deletingEventName} event`}
          description="Removing its labels from every training sample."
          progress={workspace.deletePercent}
        />
      )}
      {exportingDataset && (
        <LoadingOverlay
          title="Preparing training data"
          description={
            willTrainObject && willTrainOcr
              ? 'Building the object-detection dataset and the OCR training crops. The workspace is locked until this finishes; the console window shows details.'
              : willTrainOcr
                ? 'Collecting the text crops you labelled and rendering synthetic feed lines. The workspace is locked until this finishes; the console window shows details.'
                : 'Cropping, splitting and augmenting the object-detection dataset. The workspace is locked until this finishes; the console window shows details.'
          }
          cancelLabel="Cancel"
          onCancel={() => client.send('CancelTraining')}
        />
      )}
    </section>
  );
}
