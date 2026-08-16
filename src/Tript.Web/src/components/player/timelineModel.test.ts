// SPDX-License-Identifier: GPL-2.0-or-later
//
// The deterministic timeline geometry model: the zoom window, the time↔position mapping, and
// time formatting. These are the pure functions the two timeline levels share.

import { describe, expect, it } from 'vitest';
import {
  MIN_WINDOW_SECONDS,
  clampWindow,
  formatTime,
  niceTickInterval,
  panWindow,
  positionToTime,
  timeToPosition,
  zoomWindow,
} from './timelineModel';

describe('zoomWindow', () => {
  it('centres a default window on the focus', () => {
    expect(zoomWindow(50, 60, 120)).toEqual({ start: 20, seconds: 60 });
  });

  it('clamps the window into the session at the start', () => {
    expect(zoomWindow(5, 60, 120)).toEqual({ start: 0, seconds: 60 });
  });

  it('clamps the window into the session at the end', () => {
    expect(zoomWindow(118, 60, 120)).toEqual({ start: 60, seconds: 60 });
  });

  it('never lets the window exceed the session length', () => {
    expect(zoomWindow(60, 500, 120).seconds).toBe(120);
    expect(zoomWindow(60, 500, 120).start).toBe(0);
  });

  it('never lets the window shrink below the minimum', () => {
    expect(zoomWindow(60, 0.2, 120)).toEqual({ start: 59.5, seconds: MIN_WINDOW_SECONDS });
  });

  it('keeps the playhead inside the window after zooming out at the edge', () => {
    const w = zoomWindow(0, 60, 120);
    expect(w.start).toBe(0);
  });
});

describe('clampWindow', () => {
  it('clamps a stale window back into the session bounds', () => {
    expect(clampWindow({ start: 200, seconds: 60 }, 120)).toEqual({ start: 60, seconds: 60 });
  });

  it('shrinks an oversized window to the session length', () => {
    expect(clampWindow({ start: 10, seconds: 500 }, 120)).toEqual({ start: 0, seconds: 120 });
  });

  it('leaves a valid window untouched', () => {
    expect(clampWindow({ start: 30, seconds: 60 }, 120)).toEqual({ start: 30, seconds: 60 });
  });
});

describe('panWindow', () => {
  it('pans by the delta, preserving the window size', () => {
    expect(panWindow({ start: 30, seconds: 60 }, 10, 120)).toEqual({ start: 40, seconds: 60 });
  });

  it('clamps at the session start', () => {
    expect(panWindow({ start: 10, seconds: 60 }, -20, 120)).toEqual({ start: 0, seconds: 60 });
  });

  it('clamps at the session end', () => {
    expect(panWindow({ start: 50, seconds: 60 }, 50, 120)).toEqual({ start: 60, seconds: 60 });
  });
});

describe('position ↔ time mapping', () => {
  const rect = { left: 10, width: 100 };

  it('maps a fraction to a time', () => {
    expect(positionToTime(60, rect, 20, 40)).toBe(40);
  });

  it('maps a time to a fraction', () => {
    expect(timeToPosition(40, rect, 20, 40)).toBe(60);
  });

  it('round-trips', () => {
    const time = positionToTime(55, rect, 10, 50);
    expect(timeToPosition(time, rect, 10, 50)).toBeCloseTo(55);
  });

  it('clamps outside the bar', () => {
    expect(positionToTime(0, rect, 20, 40)).toBe(20);
    expect(positionToTime(1000, rect, 20, 40)).toBe(60);
  });
});

describe('formatTime', () => {
  it('formats minutes and seconds', () => {
    expect(formatTime(75)).toBe('1:15');
  });

  it('formats hours', () => {
    expect(formatTime(3661)).toBe('1:01:01');
  });

  it('zero pads seconds', () => {
    expect(formatTime(5)).toBe('0:05');
  });

  it('never goes negative', () => {
    expect(formatTime(-3)).toBe('0:00');
  });
});

describe('niceTickInterval', () => {
  it('picks a sensible interval for the window size', () => {
    expect(niceTickInterval(60)).toBe(10);
    expect(niceTickInterval(120)).toBe(30);
  });
});
