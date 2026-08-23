import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  TrainingEventDefinition,
  TrainingLabel,
  TrainingLabelSuggestionsMessage,
  TrainingSampleMessage,
} from '../ipc/protocol';
import { Button } from './ui/controls';
import { TrainingEventEditor } from './TrainingEventEditor';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import { useTrainingDialog } from './useTrainingDialog';
import {
  boxFromPoints,
  moveBox,
  normalizedPoint,
  resizeBottomRight,
  type TrainingPoint,
} from './trainingCoordinates';

interface TrainingSampleEditorProps {
  client: IpcClient;
  gameId: string;
  sample: TrainingSampleMessage;
  events: TrainingEventDefinition[];
  onEventsChange?: (events: TrainingEventDefinition[]) => void;
  onNavigate?: (direction: 'previous' | 'next') => void;
  canNavigatePrevious?: boolean;
  canNavigateNext?: boolean;
  hasModel?: boolean;
  onClose(): void;
}

type Gesture =
  | { kind: 'draw'; start: TrainingPoint; classId: number; index: number }
  | { kind: 'move'; index: number; start: TrainingPoint; original: TrainingLabel }
  | { kind: 'resize'; index: number; original: TrainingLabel };

function boxesOverlap(left: TrainingLabel, right: TrainingLabel): boolean {
  const leftArea = left.width * left.height;
  const rightArea = right.width * right.height;
  if (leftArea <= 0 || rightArea <= 0) return true;

  const intersectionWidth = Math.min(left.centerX + left.width / 2, right.centerX + right.width / 2)
    - Math.max(left.centerX - left.width / 2, right.centerX - right.width / 2);
  const intersectionHeight = Math.min(left.centerY + left.height / 2, right.centerY + right.height / 2)
    - Math.max(left.centerY - left.height / 2, right.centerY - right.height / 2);
  if (intersectionWidth <= 0 || intersectionHeight <= 0) return false;

  const intersection = intersectionWidth * intersectionHeight;
  return intersection / (leftArea + rightArea - intersection) >= 0.3;
}

export function TrainingSampleEditor({
  client,
  gameId,
  sample,
  events,
  onEventsChange,
  onNavigate,
  canNavigatePrevious = false,
  canNavigateNext = false,
  hasModel = false,
  onClose,
}: TrainingSampleEditorProps) {
  const [labels, setLabels] = useState<TrainingLabel[]>(sample.sample.labels);
  const [savedLabels, setSavedLabels] = useState<TrainingLabel[]>(sample.sample.labels);
  const [showSaveNotice, setShowSaveNotice] = useState(false);
  const [suggestionNotice, setSuggestionNotice] = useState<string | null>(null);
  const [isSuggesting, setIsSuggesting] = useState(false);
  const [suggestionConfidence, setSuggestionConfidence] = useState<Record<number, number>>({});
  const [eventDefinitions, setEventDefinitions] = useState(events);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [classId, setClassId] = useState(String(events[0]?.classId ?? 0));
  const [gesture, setGesture] = useState<Gesture | null>(null);
  const [eventDraft, setEventDraft] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionDraft, setRegionDraft] = useState<TrainingEventDefinition | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);
  const imageRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const suggestionRequestRef = useRef<string | null>(null);
  const labelsAreDirty = JSON.stringify(labels) !== JSON.stringify(savedLabels);

  const requestClose = () => {
    if (labelsAreDirty && !window.confirm(
      'You have unsaved changes. They will not be saved if you leave this sample. Continue?',
    )) return;
    onClose();
  };

  const dialogRef = useTrainingDialog<HTMLElement>(requestClose);

  useEffect(() => {
    setLabels(sample.sample.labels);
    setSavedLabels(sample.sample.labels);
    setShowSaveNotice(false);
    setSuggestionNotice(null);
    setIsSuggesting(false);
    setSuggestionConfidence({});
    setSelectedIndex(null);
    setGesture(null);
  }, [sample.sample.id, sample.sample.labels]);

  useEffect(() => {
    const removeSuggestions = client.on('trainingLabelSuggestions', (content) => {
      const message = content as TrainingLabelSuggestionsMessage;
      if (message.gameId !== gameId || message.sampleId !== sample.sample.id
        || message.requestId !== suggestionRequestRef.current) return;

      suggestionRequestRef.current = null;
      setIsSuggesting(false);
      setLabels((current) => {
        const additions = message.suggestions.reduce<typeof message.suggestions>((result, suggestion) => {
          if (current.some((label) => boxesOverlap(label, suggestion.label))
            || result.some((item) => boxesOverlap(item.label, suggestion.label))) return result;
          return [...result, suggestion];
        }, []);
        if (additions.length === 0) {
          setSuggestionNotice('No new labels suggested');
          return current;
        }

        const firstIndex = current.length;
        setSuggestionConfidence(Object.fromEntries(additions.map((suggestion, index) => [
          firstIndex + index,
          suggestion.confidence,
        ])));
        setSuggestionNotice(`Added ${additions.length} suggested label${additions.length === 1 ? '' : 's'}`);
        return [...current, ...additions.map((suggestion) => suggestion.label)];
      });
    });
    const removeError = client.on('error', (content) => {
      if (suggestionRequestRef.current === null) return;
      suggestionRequestRef.current = null;
      setIsSuggesting(false);
      setSuggestionNotice((content as { message?: string }).message ?? 'Could not suggest labels');
    });
    return () => {
      removeSuggestions();
      removeError();
    };
  }, [client, gameId, sample.sample.id]);

  useEffect(() => {
    if (!showSaveNotice) return;
    const timer = window.setTimeout(() => setShowSaveNotice(false), 2200);
    return () => window.clearTimeout(timer);
  }, [showSaveNotice]);

  useEffect(() => {
    setEventDefinitions(events);
    setClassId(String(events[0]?.classId ?? 0));
  }, [events]);

  useEffect(() => {
    const stage = stageRef.current;
    if (!stage || typeof ResizeObserver === 'undefined' || sample.sample.imageWidth <= 0 || sample.sample.imageHeight <= 0) return;
    const ratio = sample.sample.imageWidth / sample.sample.imageHeight;
    const updateSize = () => {
      const bounds = stage.getBoundingClientRect();
      const width = Math.min(bounds.width, bounds.height * ratio);
      setCanvasSize(width > 0 ? { width, height: width / ratio } : null);
    };
    updateSize();
    const observer = new ResizeObserver(updateSize);
    observer.observe(stage);
    return () => observer.disconnect();
  }, [sample.sample.imageHeight, sample.sample.imageWidth]);

  const pointFor = (event: React.PointerEvent): TrainingPoint | null => {
    const bounds = imageRef.current?.getBoundingClientRect();
    return bounds ? normalizedPoint(event.nativeEvent, bounds) : null;
  };

  const updateGesture = (event: React.PointerEvent) => {
    if (!gesture) return;
    const point = pointFor(event);
    if (!point) return;
    if (gesture.kind === 'draw') {
      const draft = boxFromPoints(gesture.start, point, gesture.classId);
      setLabels((current) => current.map((label, index) => index === gesture.index ? draft : label));
      return;
    }
    if (gesture.kind === 'move') {
      setLabels((current) => current.map((label, index) => index === gesture.index
        ? moveBox(gesture.original, { x: point.x - gesture.start.x, y: point.y - gesture.start.y })
        : label));
      return;
    }
    setLabels((current) => current.map((label, index) => index === gesture.index
      ? resizeBottomRight(gesture.original, point)
      : label));
  };

  const beginDraw = (event: PointerEvent) => {
    const target = event.target as HTMLElement;
    if (event.target !== event.currentTarget && target.tagName !== 'IMG') return;
    const point = pointFor(event);
    if (!point) return;
    setSelectedIndex(null);
    const index = labels.length;
    const activeClassId = Number(classId);
    setLabels((current) => [...current, boxFromPoints(point, point, activeClassId)]);
    setGesture({ kind: 'draw', start: point, classId: activeClassId, index });
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const beginMove = (event: PointerEvent, index: number) => {
    const point = pointFor(event);
    if (!point) return;
    setSelectedIndex(index);
    setClassId(String(labels[index].classId));
    setGesture({ kind: 'move', index, start: point, original: labels[index] });
    event.currentTarget.setPointerCapture(event.pointerId);
    event.stopPropagation();
  };

  const beginResize = (event: PointerEvent, index: number) => {
    setSelectedIndex(index);
    setGesture({ kind: 'resize', index, original: labels[index] });
    event.currentTarget.setPointerCapture(event.pointerId);
    event.stopPropagation();
  };

  const finishGesture = () => {
    if (gesture?.kind === 'draw') {
      const draft = labels[gesture.index];
      if (draft && draft.width > 0.001 && draft.height > 0.001) {
        setSelectedIndex(gesture.index);
      } else {
        setLabels((current) => current.filter((_, index) => index !== gesture.index));
      }
    }
    setGesture(null);
  };

  const cancelGesture = () => {
    if (gesture?.kind === 'draw') {
      setLabels((current) => current.filter((_, index) => index !== gesture.index));
    } else if (gesture?.kind === 'move' || gesture?.kind === 'resize') {
      setLabels((current) => current.map((label, index) => index === gesture.index ? gesture.original : label));
    }
    setGesture(null);
  };

  const updateSelectedClass = (value: string) => {
    setClassId(value);
    if (selectedIndex !== null) {
      setLabels((current) => current.map((label, index) => index === selectedIndex
        ? { ...label, classId: Number(value) }
        : label));
    }
  };

  const deleteSelected = () => {
    if (selectedIndex === null) return;
    setLabels((current) => current.filter((_, index) => index !== selectedIndex));
    setSuggestionConfidence((current) => Object.fromEntries(Object.entries(current)
      .filter(([index]) => Number(index) !== selectedIndex)
      .map(([index, confidence]) => [Number(index) > selectedIndex ? Number(index) - 1 : Number(index), confidence])));
    setSelectedIndex(null);
  };

  const suggestLabels = () => {
    if (!hasModel || isSuggesting) return;
    const requestId = `${sample.sample.id}-${Date.now()}`;
    suggestionRequestRef.current = requestId;
    setSuggestionNotice(null);
    setIsSuggesting(true);
    client.send('SuggestTrainingLabels', { gameId, sampleId: sample.sample.id, requestId });
  };

  const saveLabels = () => {
    client.send('UpdateTrainingSample', { gameId, sampleId: sample.sample.id, labels });
    setSavedLabels(labels.map((label) => ({ ...label })));
    setShowSaveNotice(true);
  };

  const navigate = (direction: 'previous' | 'next') => {
    if (labelsAreDirty && !window.confirm(
      'You have unsaved changes. They will not be saved if you leave this sample. Continue?',
    )) return;
    onNavigate?.(direction);
  };

  const openNewEvent = () => {
    const nextId = Math.max(0, ...eventDefinitions.map((event) => event.id)) + 1;
    const nextClassId = Math.max(-1, ...eventDefinitions.map((event) => event.classId)) + 1;
    setEventDraft({
      event: {
        id: nextId,
        classId: nextClassId,
        name: 'New event',
        type: 'Trigger',
        bookmarkType: 'Manual',
      },
      isNew: true,
    });
  };

  const saveEvent = (nextEvent: TrainingEventDefinition) => {
    const next = eventDefinitions.some((event) => event.id === nextEvent.id)
      ? eventDefinitions.map((event) => event.id === nextEvent.id ? nextEvent : event)
      : [...eventDefinitions, nextEvent];
    setEventDefinitions(next);
    setClassId(String(nextEvent.classId));
    if (onEventsChange) onEventsChange(next);
    else client.send('UpdateTrainingEvents', { gameId, events: next });
    setEventDraft(null);
  };

  const saveRegion = (nextEvent: TrainingEventDefinition) => {
    const next = eventDefinitions.map((event) => event.id === nextEvent.id ? nextEvent : event);
    onEventsChange?.(next);
    setRegionDraft(null);
  };

  return (
    <div className="training-modal-backdrop" role="presentation">
      <section ref={dialogRef} tabIndex={-1} className="training-modal" role="dialog" aria-modal="true" aria-label="Label frame">
        <header className="training-modal-header">
          <div className="training-modal-header-copy">
            <p className="training-eyebrow">Label frame</p>
            {showSaveNotice && <p className="training-save-notice" role="status">Labels saved</p>}
          </div>
          <Button variant="ghost" size="small" onClick={requestClose} aria-label="Close label frame">Close</Button>
        </header>

        <div className="training-modal-body">
          <div className="training-editor-stage" ref={stageRef}>
            <div
              className="training-editor-canvas training-editor-canvas-large"
              ref={imageRef}
              style={canvasSize ? { width: canvasSize.width, height: canvasSize.height } : undefined}
              onPointerDown={beginDraw}
              onPointerMove={updateGesture}
              onPointerUp={finishGesture}
              onPointerCancel={cancelGesture}
            >
              <img src={sample.imageData} alt="Training frame to label" draggable={false} />
              {labels.map((label, index) => (
                <span
                  className={`${index === selectedIndex ? 'training-box selected' : 'training-box'}${suggestionConfidence[index] == null ? '' : ' suggested'}`}
                  key={`${sample.sample.id}-${index}`}
                  style={{
                    left: `${(label.centerX - label.width / 2) * 100}%`,
                    top: `${(label.centerY - label.height / 2) * 100}%`,
                    width: `${label.width * 100}%`,
                    height: `${label.height * 100}%`,
                  }}
                  onPointerDown={(event) => beginMove(event, index)}
                >
                  <span className="training-box-label">
                    {eventDefinitions.find((item) => item.classId === label.classId)?.name ?? label.classId}
                    {suggestionConfidence[index] == null ? '' : ` (${Math.round(suggestionConfidence[index] * 100)}%)`}
                  </span>
                  <span className="training-box-handle" onPointerDown={(event) => beginResize(event, index)} />
                </span>
              ))}
            </div>
          </div>

          <aside className="training-event-palette" aria-label="Training events">
            <div className="training-palette-heading">
              <div>
                <p className="training-eyebrow">Events</p>
                <h3>What is in this frame?</h3>
              </div>
              <Button variant="ghost" size="small" onClick={openNewEvent} aria-label="Create event">+ Add</Button>
            </div>
            <div className="training-event-scroll">
              {eventDefinitions.map((event) => (
                <div className={Number(classId) === event.classId ? 'training-event-row active' : 'training-event-row'} key={event.id}>
                  <Button
                    variant="ghost"
                    className="training-event-select"
                    onClick={() => updateSelectedClass(String(event.classId))}
                  >
                    <span className="training-event-swatch">{event.classId}</span>
                    <span>
                      <strong>{event.name}</strong>
                      <small>{event.type}</small>
                    </span>
                  </Button>
                   <Button variant="ghost" size="small" onClick={() => setEventDraft({ event, isNew: false })}>
                     Edit
                   </Button>
                   <Button variant="ghost" size="small" onClick={() => setRegionDraft(event)}>
                     Region
                   </Button>
                </div>
              ))}
            </div>
          </aside>
        </div>

        <footer className="training-modal-footer">
          <span className="muted small">{labels.length} label{labels.length === 1 ? '' : 's'} on this frame</span>
          <div className="training-editor-actions">
            <div className="training-sample-navigation" aria-label="Sample navigation">
              <Button
                variant="ghost"
                size="small"
                onClick={() => navigate('previous')}
                disabled={!canNavigatePrevious}
              >
                Previous
              </Button>
              <Button
                variant="ghost"
                size="small"
                onClick={() => navigate('next')}
                disabled={!canNavigateNext}
              >
                Next
              </Button>
            </div>
            <Button variant="ghost" onClick={suggestLabels} disabled={!hasModel || isSuggesting}>
              {isSuggesting ? 'Suggesting...' : 'Suggest labels'}
            </Button>
            {suggestionNotice && <span className="training-save-notice" role="status">{suggestionNotice}</span>}
            <Button variant="danger" onClick={() => { client.send('DeleteTrainingSample', { gameId, sampleId: sample.sample.id }); onClose(); }}>
              Delete frame
            </Button>
            <Button variant="ghost" onClick={deleteSelected} disabled={selectedIndex === null}>Delete label</Button>
            <Button onClick={saveLabels}>Save labels</Button>
          </div>
        </footer>
      </section>
      {eventDraft && (
        <TrainingEventEditor
          key={`${eventDraft.event.id}-${eventDraft.event.name}-${eventDraft.isNew}`}
          event={eventDraft.event}
          events={eventDefinitions}
          isNew={eventDraft.isNew}
          onCancel={() => setEventDraft(null)}
          onSave={saveEvent}
        />
      )}
      {regionDraft && (
        <TrainingRegionEditor
          event={regionDraft}
          events={eventDefinitions}
          backgroundImage={sample.imageData}
          imageWidth={sample.sample.imageWidth}
          imageHeight={sample.sample.imageHeight}
          onCancel={() => setRegionDraft(null)}
          onSave={saveRegion}
        />
      )}
    </div>
  );
}
