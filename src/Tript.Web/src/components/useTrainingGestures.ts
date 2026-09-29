// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

import { useState, type Dispatch, type PointerEvent, type RefObject, type SetStateAction } from 'react';
import type { TrainingLabel, TrainingOcrRegion } from '../ipc/protocol';
import type { TrainingRegion } from './trainingRegions';
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

type Gesture =
  | { kind: 'draw'; start: TrainingPoint; classId: number; index: number }
  | { kind: 'move'; index: number; start: TrainingPoint; original: TrainingLabel }
  | { kind: 'resize'; index: number; original: TrainingLabel }
  | { kind: 'draw-ocr'; start: TrainingPoint; index: number }
  | { kind: 'move-ocr'; index: number; start: TrainingPoint; original: TrainingRegion }
  | { kind: 'resize-ocr'; index: number; original: TrainingRegion };

interface TrainingGestureOptions {
  imageRef: RefObject<HTMLDivElement | null>;
  labels: TrainingLabel[];
  setLabels: Dispatch<SetStateAction<TrainingLabel[]>>;
  ocrRegions: TrainingOcrRegion[];
  setOcrRegions: Dispatch<SetStateAction<TrainingOcrRegion[]>>;
  drawMode: 'label' | 'ocrRegion';
  canDrawLabel: boolean;
  classId: string;
  setClassId: (classId: string) => void;
  setSelectedIndex: (index: number | null) => void;
  openOcrRegionDraft: (draft: { index: number; text: string }) => void;
}

function replaceAt<T>(items: T[], at: number, next: (item: T) => T): T[] {
  return items.map((item, index) => index === at ? next(item) : item);
}

export function useTrainingGestures({
  imageRef,
  labels,
  setLabels,
  ocrRegions,
  setOcrRegions,
  drawMode,
  canDrawLabel,
  classId,
  setClassId,
  setSelectedIndex,
  openOcrRegionDraft,
}: TrainingGestureOptions) {
  const [gesture, setGesture] = useState<Gesture | null>(null);

  const pointFor = (event: PointerEvent): TrainingPoint | null => {
    const bounds = imageRef.current?.getBoundingClientRect();
    return bounds ? normalizedPoint(event.nativeEvent, bounds) : null;
  };

  const capture = (event: PointerEvent, next: Gesture) => {
    setGesture(next);
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const updateGesture = (event: PointerEvent) => {
    if (!gesture) return;
    const point = pointFor(event);
    if (!point) return;
    const offset = 'start' in gesture ? { x: point.x - gesture.start.x, y: point.y - gesture.start.y } : null;
    switch (gesture.kind) {
      case 'draw':
        setLabels((current) => replaceAt(current, gesture.index, () => boxFromPoints(gesture.start, point, gesture.classId)));
        return;
      case 'move':
        setLabels((current) => replaceAt(current, gesture.index, () => moveBox(gesture.original, offset!)));
        return;
      case 'resize':
        setLabels((current) => replaceAt(current, gesture.index, () => resizeBottomRight(gesture.original, point)));
        return;
      case 'draw-ocr': {
        const draft = { ...regionFromPoints(gesture.start, point), text: ocrRegions[gesture.index]?.text ?? '' };
        setOcrRegions((current) => replaceAt(current, gesture.index, () => draft));
        return;
      }
      case 'move-ocr':
        setOcrRegions((current) => replaceAt(current, gesture.index,
          (region) => ({ ...region, ...moveRegion(gesture.original, offset!) })));
        return;
      case 'resize-ocr':
        setOcrRegions((current) => replaceAt(current, gesture.index,
          (region) => ({ ...region, ...resizeRegionBottomRight(gesture.original, point) })));
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
      capture(event, { kind: 'draw-ocr', start: point, index });
      return;
    }
    if (!canDrawLabel) return;
    setSelectedIndex(null);
    const index = labels.length;
    const activeClassId = Number(classId);
    setLabels((current) => [...current, boxFromPoints(point, point, activeClassId)]);
    capture(event, { kind: 'draw', start: point, classId: activeClassId, index });
  };

  const beginMove = (event: PointerEvent, index: number) => {
    const point = pointFor(event);
    if (!point) return;
    setSelectedIndex(index);
    setClassId(String(labels[index].classId));
    capture(event, { kind: 'move', index, start: point, original: labels[index] });
    event.stopPropagation();
  };

  const beginResize = (event: PointerEvent, index: number) => {
    setSelectedIndex(index);
    capture(event, { kind: 'resize', index, original: labels[index] });
    event.stopPropagation();
  };

  const beginMoveOcr = (event: PointerEvent, index: number) => {
    const point = pointFor(event);
    if (!point) return;
    capture(event, { kind: 'move-ocr', index, start: point, original: ocrRegions[index] });
    event.stopPropagation();
  };

  const beginResizeOcr = (event: PointerEvent, index: number) => {
    capture(event, { kind: 'resize-ocr', index, original: ocrRegions[index] });
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
        openOcrRegionDraft({ index: gesture.index, text: draft.text });
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
      setLabels((current) => replaceAt(current, gesture.index, () => gesture.original));
    } else if (gesture?.kind === 'draw-ocr') {
      setOcrRegions((current) => current.filter((_, index) => index !== gesture.index));
    } else if (gesture?.kind === 'move-ocr' || gesture?.kind === 'resize-ocr') {
      setOcrRegions((current) => replaceAt(current, gesture.index,
        (region) => ({ ...gesture.original, text: region.text })));
    }
    setGesture(null);
  };

  return {
    updateGesture,
    beginDraw,
    beginMove,
    beginResize,
    beginMoveOcr,
    beginResizeOcr,
    finishGesture,
    cancelGesture,
    clearGesture: () => setGesture(null),
  };
}
