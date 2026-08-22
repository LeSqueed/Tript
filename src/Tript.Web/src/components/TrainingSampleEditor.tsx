import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  TrainingEventDefinition,
  TrainingLabel,
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
  onClose(): void;
}

type Gesture =
  | { kind: 'draw'; start: TrainingPoint; classId: number; index: number }
  | { kind: 'move'; index: number; start: TrainingPoint; original: TrainingLabel }
  | { kind: 'resize'; index: number; original: TrainingLabel };

export function TrainingSampleEditor({ client, gameId, sample, events, onEventsChange, onClose }: TrainingSampleEditorProps) {
  const [labels, setLabels] = useState<TrainingLabel[]>(sample.sample.labels);
  const [eventDefinitions, setEventDefinitions] = useState(events);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const [classId, setClassId] = useState(String(events[0]?.classId ?? 0));
  const [gesture, setGesture] = useState<Gesture | null>(null);
  const [eventDraft, setEventDraft] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionDraft, setRegionDraft] = useState<TrainingEventDefinition | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);
  const imageRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const dialogRef = useTrainingDialog<HTMLElement>(onClose);

  useEffect(() => {
    setLabels(sample.sample.labels);
    setSelectedIndex(null);
    setGesture(null);
  }, [sample.sample.id, sample.sample.labels]);

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
    setSelectedIndex(null);
  };

  const saveLabels = () => {
    client.send('UpdateTrainingSample', { gameId, sampleId: sample.sample.id, labels });
    onClose();
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
        lifetimeMs: null,
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
          <p className="training-eyebrow">Label frame</p>
          <Button variant="ghost" size="small" onClick={onClose} aria-label="Close label frame">Close</Button>
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
                  className={index === selectedIndex ? 'training-box selected' : 'training-box'}
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
