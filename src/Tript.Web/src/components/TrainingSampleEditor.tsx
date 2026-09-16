import { useEffect, useRef, useState, type PointerEvent } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  TrainingEventDefinition,
  TrainingLabel,
  TrainingLabelSuggestionsMessage,
  TrainingOcrRegion,
  TrainingRegionGroup,
  TrainingSampleMessage,
  TrainingUpdateResultMessage,
} from '../ipc/protocol';
import { Button, Field, TextField } from './ui/controls';
import { TrainingEventEditor } from './TrainingEventEditor';
import { TrainingEventTree } from './TrainingEventTree';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import { effectiveTrainingRegion, isLabelInsideEffectiveRegion, type TrainingRegion } from './trainingRegions';
import { useTrainingDialog } from './useTrainingDialog';
import {
  boxFromPoints,
  moveBox,
  moveRegion,
  normalizedPoint,
  regionFromPoints,
  resizeBottomRight,
  resizeRegionBottomRight,
  type TrainingPoint,
} from './trainingCoordinates';

interface TrainingSampleEditorProps {
  client: IpcClient;
  gameId: string;
  sample: TrainingSampleMessage;
  events: TrainingEventDefinition[];
  regionGroups?: TrainingRegionGroup[];
  onEventsChange?: (events: TrainingEventDefinition[], requestId: string) => void;
  onRegionGroupsChange?: (groups: TrainingRegionGroup[], requestId: string) => void;
  onNavigate?: (direction: 'previous' | 'next') => void;
  canNavigatePrevious?: boolean;
  canNavigateNext?: boolean;
  hasModel?: boolean;
  onClose(): void;
}

type Gesture =
  | { kind: 'draw'; start: TrainingPoint; classId: number; index: number }
  | { kind: 'move'; index: number; start: TrainingPoint; original: TrainingLabel }
  | { kind: 'resize'; index: number; original: TrainingLabel }
  | { kind: 'draw-ocr'; start: TrainingPoint; index: number }
  | { kind: 'move-ocr'; index: number; start: TrainingPoint; original: TrainingRegion }
  | { kind: 'resize-ocr'; index: number; original: TrainingRegion };

const EMPTY_REGION_GROUPS: TrainingRegionGroup[] = [];

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

function fixedLabelFor(event: TrainingEventDefinition): TrainingLabel | null {
  const values = [event.fixedLabelCenterX, event.fixedLabelCenterY, event.fixedLabelWidth, event.fixedLabelHeight];
  if (!event.fixedPosition || values.some((value) => value == null || !Number.isFinite(value))) return null;
  const [centerX, centerY, width, height] = values as number[];
  return { classId: event.classId, centerX, centerY, width, height };
}

export function TrainingSampleEditor({
  client,
  gameId,
  sample,
  events,
  regionGroups,
  onEventsChange,
  onRegionGroupsChange,
  onNavigate,
  canNavigatePrevious = false,
  canNavigateNext = false,
  hasModel = false,
  onClose,
}: TrainingSampleEditorProps) {
  const [labels, setLabels] = useState<TrainingLabel[]>(sample.sample.labels);
  const [savedLabels, setSavedLabels] = useState<TrainingLabel[]>(sample.sample.labels);
  const [ocrRegions, setOcrRegions] = useState<TrainingOcrRegion[]>(
    sample.sample.ocrRegions ?? [],
  );
  const [savedOcrRegions, setSavedOcrRegions] = useState<TrainingOcrRegion[]>(
    sample.sample.ocrRegions ?? [],
  );
  const [showSaveNotice, setShowSaveNotice] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState(false);
  const [suggestionNotice, setSuggestionNotice] = useState<string | null>(null);
  const [isSuggesting, setIsSuggesting] = useState(false);
  const [suggestionConfidence, setSuggestionConfidence] = useState<Record<number, number>>({});
  const [eventDefinitions, setEventDefinitions] = useState(events);
  const resolvedRegionGroups = regionGroups ?? EMPTY_REGION_GROUPS;
  const [groupDefinitions, setGroupDefinitions] = useState(resolvedRegionGroups);
  const [selectedIndex, setSelectedIndex] = useState<number | null>(null);
  const firstObjectEvent = events.find((event) => (event.detectionKind ?? 'Object') === 'Object');
  const [classId, setClassId] = useState(String(sample.sample.labels[0]?.classId ?? firstObjectEvent?.classId ?? 0));
  const [gesture, setGesture] = useState<Gesture | null>(null);
  const [drawMode, setDrawMode] = useState<'label' | 'ocrRegion'>('label');
  const [eventDraft, setEventDraft] = useState<{ event: TrainingEventDefinition; isNew: boolean } | null>(null);
  const [regionDraft, setRegionDraft] = useState<
    { target: TrainingEventDefinition; targetType: 'event' }
    | { target: TrainingRegionGroup; targetType: 'group' }
    | null
  >(null);
  const [ocrRegionDraft, setOcrRegionDraft] = useState<{ index: number; text: string } | null>(null);
  const [canvasSize, setCanvasSize] = useState<{ width: number; height: number } | null>(null);
  const imageRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const suggestionRequestRef = useRef<string | null>(null);
  const pendingSaveRef = useRef<{
    requestId: string;
    labels: TrainingLabel[];
    ocrRegions: TrainingOcrRegion[];
  } | null>(null);
  const requestCounterRef = useRef(0);
  const labelsAreDirty = JSON.stringify(labels) !== JSON.stringify(savedLabels)
    || JSON.stringify(ocrRegions) !== JSON.stringify(savedOcrRegions);
  const invalidLabelIndexes = new Set(labels.flatMap((label, index) =>
    isLabelInsideEffectiveRegion(label, eventDefinitions, groupDefinitions) ? [] : [index]));
  const activeEvent = eventDefinitions.find((event) => (event.detectionKind ?? 'Object') === 'Object'
    && event.classId === (
    selectedIndex == null ? Number(classId) : labels[selectedIndex]?.classId
  ));
  const activeRegion = activeEvent ? effectiveTrainingRegion(activeEvent, groupDefinitions) : null;

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
    setOcrRegions(sample.sample.ocrRegions ?? []);
    setSavedOcrRegions(sample.sample.ocrRegions ?? []);
    setDrawMode('label');
    setOcrRegionDraft(null);
    setShowSaveNotice(false);
    setSaveError(null);
    setIsSaving(false);
    pendingSaveRef.current = null;
    setSuggestionNotice(null);
    setIsSuggesting(false);
    setSuggestionConfidence({});
    setSelectedIndex(null);
    setGesture(null);
    if (sample.sample.labels[0]) setClassId(String(sample.sample.labels[0].classId));
  }, [sample.sample.id, sample.sample.labels, sample.sample.ocrRegions]);

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
    const removeSaveResult = client.on('trainingSampleUpdateResult', (content) => {
      const message = content as TrainingUpdateResultMessage;
      const pending = pendingSaveRef.current;
      if (!pending || message.requestId !== pending.requestId) return;
      pendingSaveRef.current = null;
      setIsSaving(false);
      if (message.success) {
        setSavedLabels(pending.labels);
        setSavedOcrRegions(pending.ocrRegions);
        setSaveError(null);
        setShowSaveNotice(true);
      } else {
        setSaveError(message.error ?? 'Could not save labels');
      }
    });
    const removeError = client.on('error', (content) => {
      const message = (content as { message?: string }).message;
      if (suggestionRequestRef.current !== null) {
        suggestionRequestRef.current = null;
        setIsSuggesting(false);
        setSuggestionNotice(message ?? 'Could not suggest labels');
      }
    });
    return () => {
      removeSuggestions();
      removeSaveResult();
      removeError();
    };
  }, [client, gameId, sample.sample.id]);

  useEffect(() => {
    if (!showSaveNotice) return;
    const timer = window.setTimeout(() => setShowSaveNotice(false), 2200);
    return () => window.clearTimeout(timer);
  }, [showSaveNotice]);

  const ocrRegionDraftOpen = ocrRegionDraft !== null;
  useEffect(() => {
    if (!ocrRegionDraftOpen) return;
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      closeOcrRegionDraft();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [ocrRegionDraftOpen]);

  useEffect(() => {
    setEventDefinitions(events);
    const objectEvents = events.filter((event) => (event.detectionKind ?? 'Object') === 'Object');
    const validClassIds = new Set(objectEvents.map((event) => event.classId));
    const keep = (list: TrainingLabel[]) => list.filter((label) => validClassIds.has(label.classId));
    setLabels((current) => keep(current));
    setSavedLabels((current) => keep(current));
    setClassId((current) => objectEvents.some((event) => event.classId === Number(current))
      ? current
      : String(objectEvents[0]?.classId ?? 0));
  }, [events]);

  useEffect(() => {
    setGroupDefinitions(resolvedRegionGroups);
  }, [resolvedRegionGroups]);

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
    if (gesture.kind === 'resize') {
      setLabels((current) => current.map((label, index) => index === gesture.index
        ? resizeBottomRight(gesture.original, point)
        : label));
      return;
    }
    if (gesture.kind === 'draw-ocr') {
      const draft = { ...regionFromPoints(gesture.start, point), text: ocrRegions[gesture.index]?.text ?? '' };
      setOcrRegions((current) => current.map((region, index) => index === gesture.index ? draft : region));
      return;
    }
    if (gesture.kind === 'move-ocr') {
      setOcrRegions((current) => current.map((region, index) => index === gesture.index
        ? { ...region, ...moveRegion(gesture.original, { x: point.x - gesture.start.x, y: point.y - gesture.start.y }) }
        : region));
      return;
    }
    if (gesture.kind === 'resize-ocr') {
      setOcrRegions((current) => current.map((region, index) => index === gesture.index
        ? { ...region, ...resizeRegionBottomRight(gesture.original, point) }
        : region));
    }
  };

  const beginDraw = (event: PointerEvent) => {
    const target = event.target as HTMLElement;
    if (event.target !== event.currentTarget && target.tagName !== 'IMG') return;
    const point = pointFor(event);
    if (!point) return;
    if (drawMode === 'ocrRegion') {
      setSelectedIndex(null);
      const index = ocrRegions.length;
      setOcrRegions((current) => [...current, { ...regionFromPoints(point, point), text: '' }]);
      setGesture({ kind: 'draw-ocr', start: point, index });
      event.currentTarget.setPointerCapture(event.pointerId);
      return;
    }
    if (!activeEvent) return;
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

  const beginMoveOcr = (event: PointerEvent, index: number) => {
    const point = pointFor(event);
    if (!point) return;
    setGesture({ kind: 'move-ocr', index, start: point, original: ocrRegions[index] });
    event.currentTarget.setPointerCapture(event.pointerId);
    event.stopPropagation();
  };

  const beginResizeOcr = (event: PointerEvent, index: number) => {
    setGesture({ kind: 'resize-ocr', index, original: ocrRegions[index] });
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
    } else if (gesture?.kind === 'draw-ocr') {
      const draft = ocrRegions[gesture.index];
      if (draft && draft.width > 0.002 && draft.height > 0.002) {
        setOcrRegionDraft({ index: gesture.index, text: draft.text });
      } else {
        setOcrRegions((current) => current.filter((_, index) => index !== gesture.index));
      }
    }
    setGesture(null);
  };

  const cancelGesture = () => {
    if (gesture?.kind === 'draw') {
      setLabels((current) => current.filter((_, index) => index !== gesture.index));
    } else if (gesture?.kind === 'move' || gesture?.kind === 'resize') {
      setLabels((current) => current.map((label, index) => index === gesture.index ? gesture.original : label));
    } else if (gesture?.kind === 'draw-ocr') {
      setOcrRegions((current) => current.filter((_, index) => index !== gesture.index));
    } else if (gesture?.kind === 'move-ocr' || gesture?.kind === 'resize-ocr') {
      setOcrRegions((current) => current.map((region, index) =>
        index === gesture.index ? { ...gesture.original, text: region.text } : region));
    }
    setGesture(null);
  };

  const updateSelectedClass = (value: string) => {
    const target = eventDefinitions.find((candidate) => candidate.classId === Number(value));
    if (!target || (target.detectionKind ?? 'Object') !== 'Object') return;
    setClassId(value);
    if (selectedIndex !== null) {
      const fixedLabel = fixedLabelFor(target);
      setLabels((current) => current.map((label, index) => index === selectedIndex
        ? fixedLabel ?? { ...label, classId: Number(value) }
        : label));
    }
  };

  const addFixedLabel = (event: TrainingEventDefinition) => {
    const existing = labels.findIndex((label) => label.classId === event.classId);
    if (existing >= 0) {
      setSelectedIndex(existing);
      setClassId(String(event.classId));
      return;
    }
    const fixedLabel = fixedLabelFor(event);
    if (!fixedLabel) return;
    setLabels((current) => [...current, fixedLabel]);
    setSelectedIndex(labels.length);
    setClassId(String(event.classId));
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
    if (isSaving) return;
    const validClassIds = new Set(events
      .filter((event) => (event.detectionKind ?? 'Object') === 'Object')
      .map((event) => event.classId));
    const submittedLabels = labels
      .filter((label) => validClassIds.has(label.classId))
      .map((label) => ({ ...label }));
    const submittedOcrRegions = ocrRegions
      .filter((region) => region.text.trim())
      .map((region) => ({
        x: region.x, y: region.y, width: region.width, height: region.height,
        text: region.text.trim(),
      }));
    const requestId = `sample-${sample.sample.id}-${++requestCounterRef.current}`;
    pendingSaveRef.current = {
      requestId,
      labels: submittedLabels,
      ocrRegions: submittedOcrRegions,
    };
    setIsSaving(true);
    setSaveError(null);
    setShowSaveNotice(false);
    client.send('UpdateTrainingSample', {
      gameId,
      sampleId: sample.sample.id,
      requestId,
      labels: submittedLabels,
      ocrRegions: submittedOcrRegions,
    });
  };

  const navigate = (direction: 'previous' | 'next') => {
    if (labelsAreDirty && !window.confirm(
      'You have unsaved changes. They will not be saved if you leave this sample. Continue?',
    )) return;
    onNavigate?.(direction);
  };

  const openNewEvent = () => {
    const nextId = Math.max(0, ...eventDefinitions.map((event) => event.id)) + 1;
    const nextClassId = Math.max(-1, ...eventDefinitions
      .filter((event) => (event.detectionKind ?? 'Object') === 'Object')
      .map((event) => event.classId)) + 1;
    setEventDraft({
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
    const next = eventDefinitions.some((event) => event.id === nextEvent.id)
      ? eventDefinitions.map((event) => event.id === nextEvent.id ? nextEvent : event)
      : [...eventDefinitions, nextEvent];
    setEventDefinitions(next);
    setClassId(String(nextEvent.classId));
    const requestId = `events-editor-${++requestCounterRef.current}`;
    if (onEventsChange) onEventsChange(next, requestId);
    else client.send('UpdateTrainingEvents', { gameId, requestId, events: next });
    setEventDraft(null);
  };

  const updateEventGroup = (eventId: number, value: string) => {
    const next = eventDefinitions.map((event) => event.id === eventId
      ? { ...event, regionGroupId: value ? Number(value) : null }
      : event);
    setEventDefinitions(next);
    const requestId = `events-editor-${++requestCounterRef.current}`;
    if (onEventsChange) onEventsChange(next, requestId);
    else client.send('UpdateTrainingEvents', { gameId, requestId, events: next });
  };

  const openRegion = (event: TrainingEventDefinition) => {
    const group = event.regionGroupId == null
      ? undefined
      : groupDefinitions.find((candidate) => candidate.id === event.regionGroupId);
    setRegionDraft(group
      ? { target: group, targetType: 'group' }
      : { target: event, targetType: 'event' });
  };

  const saveRegion = (target: TrainingEventDefinition | TrainingRegionGroup) => {
    if (regionDraft?.targetType === 'group') {
      const next = groupDefinitions.map((group) => group.id === target.id ? target as TrainingRegionGroup : group);
      setGroupDefinitions(next);
      const requestId = `region-groups-editor-${++requestCounterRef.current}`;
      if (onRegionGroupsChange) onRegionGroupsChange(next, requestId);
      else client.send('UpdateTrainingRegionGroups', { gameId, requestId, regionGroups: next });
    } else {
      const next = eventDefinitions.map((event) => event.id === target.id ? target as TrainingEventDefinition : event);
      setEventDefinitions(next);
      const requestId = `events-editor-${++requestCounterRef.current}`;
      if (onEventsChange) onEventsChange(next, requestId);
      else client.send('UpdateTrainingEvents', { gameId, requestId, events: next });
    }
    setRegionDraft(null);
  };

  const openOcrRegionText = (index: number) => {
    setOcrRegionDraft({ index, text: ocrRegions[index]?.text ?? '' });
  };

  const closeOcrRegionDraft = () => {
    const draft = ocrRegionDraft;
    if (draft && !ocrRegions[draft.index]?.text.trim()) {
      setOcrRegions((current) => current.filter((_, index) => index !== draft.index));
    }
    setOcrRegionDraft(null);
  };

  const saveOcrRegionText = () => {
    const draft = ocrRegionDraft;
    if (!draft) return;
    const text = draft.text.trim();
    setOcrRegions((current) => text
      ? current.map((region, index) => index === draft.index ? { ...region, text } : region)
      : current.filter((_, index) => index !== draft.index));
    setOcrRegionDraft(null);
  };

  const removeOcrRegion = () => {
    const draft = ocrRegionDraft;
    if (!draft) return;
    setOcrRegions((current) => current.filter((_, index) => index !== draft.index));
    setOcrRegionDraft(null);
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
              {activeRegion && (
                <span
                  className="training-effective-region"
                  aria-label={`Effective region for ${activeEvent?.name}`}
                  style={{
                    left: `${activeRegion.x * 100}%`,
                    top: `${activeRegion.y * 100}%`,
                    width: `${activeRegion.width * 100}%`,
                    height: `${activeRegion.height * 100}%`,
                  }}
                />
              )}
              {labels.map((label, index) => (
                <span
                  className={`${index === selectedIndex ? 'training-box selected' : 'training-box'}${suggestionConfidence[index] == null ? '' : ' suggested'}${invalidLabelIndexes.has(index) ? ' invalid' : ''}`}
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
              {ocrRegions.map((region, index) => (
                <span
                  key={`ocr-region-${index}`}
                  className="training-ocr-region"
                  aria-label={`OCR region: ${region.text || 'untitled'}`}
                  style={{
                    left: `${region.x * 100}%`,
                    top: `${region.y * 100}%`,
                    width: `${region.width * 100}%`,
                    height: `${region.height * 100}%`,
                  }}
                  onPointerDown={(event) => beginMoveOcr(event, index)}
                >
                  <button
                    type="button"
                    className="training-ocr-region-caption"
                    onPointerDown={(event) => event.stopPropagation()}
                    onClick={() => openOcrRegionText(index)}
                  >
                    {region.text || 'Add text'}
                  </button>
                  <span className="training-box-handle" onPointerDown={(event) => beginResizeOcr(event, index)} />
                </span>
              ))}
            </div>
          </div>

          <aside className="training-event-palette" aria-label="Training events">
            <div className="training-palette-top">
              <div className="training-palette-heading">
                <div>
                  <p className="training-eyebrow">Events</p>
                  <h3>What is in this frame?</h3>
                </div>
                <Button variant="ghost" size="small" onClick={openNewEvent} aria-label="Create event">+ Add</Button>
              </div>
              <div className={`training-ocr-tool${drawMode === 'ocrRegion' ? ' is-armed' : ''}`}>
                <button
                  type="button"
                  className="training-ocr-tool-toggle"
                  aria-pressed={drawMode === 'ocrRegion'}
                  onClick={() => setDrawMode((mode) => mode === 'ocrRegion' ? 'label' : 'ocrRegion')}
                >
                  <span className="training-ocr-tool-icon" aria-hidden="true" />
                  {drawMode === 'ocrRegion' ? 'Drawing: drag on the frame' : 'Add OCR region'}
                </button>
                <span className="training-ocr-tool-count">
                  {ocrRegions.length} region{ocrRegions.length === 1 ? '' : 's'}
                </span>
              </div>
            </div>
            <div className="training-event-scroll">
              <TrainingEventTree
                compact
                events={eventDefinitions}
                groups={groupDefinitions}
                activeClassId={Number(classId)}
                onSelect={(event) => updateSelectedClass(String(event.classId))}
                onEdit={(event) => setEventDraft({ event, isNew: false })}
                onRegion={openRegion}
                onAddFixedLabel={addFixedLabel}
                canAddFixedLabel={(event) => fixedLabelFor(event) !== null
                  && !labels.some((label) => label.classId === event.classId)}
                onMove={(eventId, groupId) => updateEventGroup(eventId, groupId == null ? '' : String(groupId))}
                onRegionGroup={(group) => setRegionDraft({ target: group, targetType: 'group' })}
              />
            </div>
          </aside>
        </div>

        <footer className="training-modal-footer">
          <span className="muted small">{labels.length} label{labels.length === 1 ? '' : 's'} on this frame</span>
          {invalidLabelIndexes.size > 0 && (
            <span className="training-label-warning" role="alert">
              {invalidLabelIndexes.size} label{invalidLabelIndexes.size === 1 ? '' : 's'} outside the effective event region and will be skipped when training.
            </span>
          )}
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
            {saveError && <span className="training-label-warning" role="alert">{saveError}</span>}
            <Button onClick={saveLabels} disabled={isSaving}>{isSaving ? 'Saving...' : 'Save labels'}</Button>
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
          target={regionDraft.target}
          targetType={regionDraft.targetType}
          backgroundImage={sample.imageData}
          imageWidth={sample.sample.imageWidth}
          imageHeight={sample.sample.imageHeight}
          onCancel={() => setRegionDraft(null)}
          onSave={saveRegion}
        />
      )}
      {ocrRegionDraft && (
        <div className="training-ocr-text-overlay" role="presentation">
          <section className="training-ocr-text-dialog" role="dialog" aria-modal="true" aria-labelledby="ocr-text-title">
            <div className="training-palette-heading">
              <div>
                <p className="training-eyebrow">OCR region</p>
                <h3 id="ocr-text-title">Text in this box</h3>
              </div>
            </div>
            <p className="muted small">
              Type the exact text you can read inside the box. It is the ground truth that teaches the recogniser.
            </p>
            <Field label="Text">
              <TextField
                autoFocus
                value={ocrRegionDraft.text}
                placeholder="Exact text visible in this box"
                onChange={(value) => setOcrRegionDraft((draft) => draft ? { ...draft, text: value } : draft)}
              />
            </Field>
            <div className="training-event-dialog-actions">
              {ocrRegions[ocrRegionDraft.index]?.text.trim() && (
                <Button variant="danger" onClick={removeOcrRegion}>Remove</Button>
              )}
              <Button variant="ghost" onClick={closeOcrRegionDraft}>Cancel</Button>
              <Button onClick={saveOcrRegionText} disabled={!ocrRegionDraft.text.trim()}>Save</Button>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}
