import type { TrainingEventDefinition, TrainingLabel, TrainingRegionGroup } from '../ipc/protocol';

export interface TrainingRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

export const REGION_CONTAINMENT_EPSILON = 0.000001;

export function regionFromTarget(
  target: Pick<TrainingEventDefinition, 'screenRegionX' | 'screenRegionY' | 'screenRegionW' | 'screenRegionH'>,
): TrainingRegion | null {
  const values = [target.screenRegionX, target.screenRegionY, target.screenRegionW, target.screenRegionH];
  if (values.some((value) => value == null || !Number.isFinite(value))) return null;
  const [x, y, width, height] = values as number[];
  if (width <= 0 || height <= 0 || x < 0 || y < 0 || x + width > 1 || y + height > 1) return null;
  return { x, y, width, height };
}

export function effectiveTrainingRegion(
  event: TrainingEventDefinition,
  groups: TrainingRegionGroup[],
): TrainingRegion | null {
  const group = event.regionGroupId == null
    ? undefined
    : groups.find((candidate) => candidate.id === event.regionGroupId);
  return regionFromTarget(group ?? event);
}

export function isLabelInsideRegion(label: TrainingLabel, region: TrainingRegion | null): boolean {
  if (![label.centerX, label.centerY, label.width, label.height].every(Number.isFinite)
    || label.width <= 0 || label.height <= 0) return false;
  const imageLeft = label.centerX - label.width / 2;
  const imageRight = label.centerX + label.width / 2;
  const imageTop = label.centerY - label.height / 2;
  const imageBottom = label.centerY + label.height / 2;
  if (imageLeft < 0 || imageTop < 0 || imageRight > 1 || imageBottom > 1) return false;
  if (!region) return true;
  return imageLeft + REGION_CONTAINMENT_EPSILON >= region.x
    && imageTop + REGION_CONTAINMENT_EPSILON >= region.y
    && imageRight <= region.x + region.width + REGION_CONTAINMENT_EPSILON
    && imageBottom <= region.y + region.height + REGION_CONTAINMENT_EPSILON;
}

export function isLabelInsideEffectiveRegion(
  label: TrainingLabel,
  events: TrainingEventDefinition[],
  groups: TrainingRegionGroup[],
): boolean {
  const event = events.find((candidate) => candidate.classId === label.classId);
  if (!event || (event.regionGroupId != null
    && !groups.some((group) => group.id === event.regionGroupId))) return false;
  return isLabelInsideRegion(label, effectiveTrainingRegion(event, groups));
}
