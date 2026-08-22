import { describe, expect, it } from 'vitest';
import { boxFromPoints, moveBox, resizeBottomRight } from './trainingCoordinates';

describe('training coordinate editing', () => {
  it('creates normalized boxes regardless of drag direction', () => {
    const box = boxFromPoints({ x: 0.8, y: 0.7 }, { x: 0.2, y: 0.1 }, 3);
    expect(box.classId).toBe(3);
    expect(box.centerX).toBeCloseTo(0.5);
    expect(box.centerY).toBeCloseTo(0.4);
    expect(box.width).toBeCloseTo(0.6);
    expect(box.height).toBeCloseTo(0.6);
  });

  it('keeps moved boxes inside the image', () => {
    const box = { classId: 0, centerX: 0.5, centerY: 0.5, width: 0.4, height: 0.2 };
    expect(moveBox(box, { x: 1, y: -1 })).toEqual({
      ...box,
      centerX: 0.8,
      centerY: 0.1,
    });
  });

  it('resizes from the original top-left edge and clamps the handle', () => {
    const box = { classId: 0, centerX: 0.3, centerY: 0.3, width: 0.2, height: 0.2 };
    expect(resizeBottomRight(box, { x: 2, y: 2 })).toEqual({
      classId: 0,
      centerX: 0.6,
      centerY: 0.6,
      width: 0.8,
      height: 0.8,
    });
  });
});
