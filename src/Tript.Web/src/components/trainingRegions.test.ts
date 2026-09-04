import { describe, expect, it } from 'vitest';
import {
  effectiveTrainingRegion,
  isLabelInsideEffectiveRegion,
  isLabelInsideRegion,
} from './trainingRegions';

describe('trainingRegions', () => {
  it('uses a group region instead of the event region', () => {
    const region = effectiveTrainingRegion({
      id: 1,
      classId: 0,
      name: 'Event',
      type: 'Trigger',
      regionGroupId: 4,
      screenRegionX: 0.1,
      screenRegionY: 0.1,
      screenRegionW: 0.2,
      screenRegionH: 0.2,
    }, [{
      id: 4,
      name: 'HUD',
      screenRegionX: 0.5,
      screenRegionY: 0.5,
      screenRegionW: 0.4,
      screenRegionH: 0.4,
    }]);

    expect(region).toEqual({ x: 0.5, y: 0.5, width: 0.4, height: 0.4 });
  });

  it('accepts tiny floating-point boundary drift but rejects an outside label', () => {
    const region = { x: 0.2, y: 0.2, width: 0.4, height: 0.4 };
    expect(isLabelInsideRegion({
      classId: 0, centerX: 0.4, centerY: 0.4, width: 0.400001, height: 0.400001,
    }, region)).toBe(true);
    expect(isLabelInsideRegion({
      classId: 0, centerX: 0.4, centerY: 0.4, width: 0.41, height: 0.4,
    }, region)).toBe(false);
  });

  it('rejects malformed image geometry and missing group references', () => {
    expect(isLabelInsideRegion({
      classId: 0, centerX: 0.98, centerY: 0.5, width: 0.1, height: 0.1,
    }, null)).toBe(false);
    expect(isLabelInsideRegion({
      classId: 0, centerX: 0.5, centerY: 0.5, width: Number.NaN, height: 0.1,
    }, null)).toBe(false);
    expect(isLabelInsideEffectiveRegion({
      classId: 0, centerX: 0.5, centerY: 0.5, width: 0.1, height: 0.1,
    }, [{
      id: 1, classId: 0, name: 'Event', type: 'Trigger', regionGroupId: 99,
    }], [])).toBe(false);
  });
});
