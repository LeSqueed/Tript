import type { TrainingLabel } from '../ipc/protocol';
import type { TrainingRegion } from './trainingRegions';

export interface TrainingPoint {
  x: number;
  y: number;
}

export function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

export function normalizedPoint(event: PointerEvent, bounds: DOMRect): TrainingPoint {
  return {
    x: clamp((event.clientX - bounds.left) / bounds.width, 0, 1),
    y: clamp((event.clientY - bounds.top) / bounds.height, 0, 1),
  };
}

export function boxFromPoints(start: TrainingPoint, end: TrainingPoint, classId: number): TrainingLabel {
  const left = Math.min(start.x, end.x);
  const top = Math.min(start.y, end.y);
  const right = Math.max(start.x, end.x);
  const bottom = Math.max(start.y, end.y);
  return {
    classId,
    centerX: (left + right) / 2,
    centerY: (top + bottom) / 2,
    width: right - left,
    height: bottom - top,
  };
}

export function moveBox(box: TrainingLabel, delta: TrainingPoint): TrainingLabel {
  const halfWidth = box.width / 2;
  const halfHeight = box.height / 2;
  return {
    ...box,
    centerX: clamp(box.centerX + delta.x, halfWidth, 1 - halfWidth),
    centerY: clamp(box.centerY + delta.y, halfHeight, 1 - halfHeight),
  };
}

export function resizeBottomRight(box: TrainingLabel, point: TrainingPoint): TrainingLabel {
  const left = box.centerX - box.width / 2;
  const top = box.centerY - box.height / 2;
  const right = clamp(point.x, left + 0.001, 1);
  const bottom = clamp(point.y, top + 0.001, 1);
  return {
    ...box,
    centerX: (left + right) / 2,
    centerY: (top + bottom) / 2,
    width: right - left,
    height: bottom - top,
  };
}

export function regionFromPoints(start: TrainingPoint, end: TrainingPoint): TrainingRegion {
  const x = Math.min(start.x, end.x);
  const y = Math.min(start.y, end.y);
  return { x, y, width: Math.abs(end.x - start.x), height: Math.abs(end.y - start.y) };
}

export function moveRegion(region: TrainingRegion, delta: TrainingPoint): TrainingRegion {
  return {
    ...region,
    x: clamp(region.x + delta.x, 0, 1 - region.width),
    y: clamp(region.y + delta.y, 0, 1 - region.height),
  };
}

export function resizeRegionBottomRight(region: TrainingRegion, point: TrainingPoint): TrainingRegion {
  const right = clamp(point.x, region.x + 0.001, 1);
  const bottom = clamp(point.y, region.y + 0.001, 1);
  return { ...region, width: right - region.x, height: bottom - region.y };
}
