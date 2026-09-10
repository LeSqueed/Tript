// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { TimelineRegion } from './clipSeam';
import { computeEditSeek, computeLoopDecision } from './clipLoop';

const region = (id: string, start: number, end: number): TimelineRegion => ({ id, start, end });

describe('computeLoopDecision', () => {
  it('does not loop while paused, even inside a selected segment', () => {
    const decision = computeLoopDecision(30, 20, false, [region('r1', 10, 50)], 'r1');
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('does not loop when no segment is selected', () => {
    const decision = computeLoopDecision(30, 20, true, [region('r1', 10, 50)], null);
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('does not loop when inside a marked segment that is not the selected one', () => {
    const decision = computeLoopDecision(65, 64, true, [region('r1', 10, 50), region('r2', 60, 70)], 'r1');
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('does not loop when the selected segment no longer exists', () => {
    const decision = computeLoopDecision(30, 20, true, [region('r1', 10, 50)], 'gone');
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('inside the selected segment before its end: playing normally', () => {
    const decision = computeLoopDecision(30, 20, true, [region('r1', 10, 50)], 'r1');
    expect(decision).toEqual({ region: region('r1', 10, 50), shouldLoopBack: false });
  });

  it('crossing the segment END while inside it: loops back to the segment start', () => {
    const decision = computeLoopDecision(50, 49, true, [region('r1', 10, 50)], 'r1');
    expect(decision.region).toEqual(region('r1', 10, 50));
    expect(decision.shouldLoopBack).toBe(true);
  });

  it('leaving the segment with a deliberate seek past its end resumes normal playback', () => {
    const decision = computeLoopDecision(51, 45, true, [region('r1', 10, 50)], 'r1');
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('before the segment start resumes normal playback', () => {
    const decision = computeLoopDecision(5, 4, true, [region('r1', 10, 50)], 'r1');
    expect(decision).toEqual({ region: null, shouldLoopBack: false });
  });

  it('a single-sample jump landing exactly at the end does not loop', () => {
    const decision = computeLoopDecision(50, 10, true, [region('r1', 10, 50)], 'r1');
    expect(decision.shouldLoopBack).toBe(false);
  });
});

describe('computeEditSeek', () => {
  it('the end dragged before the playhead loops back to the start', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 44), 46)).toBe(37);
  });

  it('the end dragged later, still ahead of the playhead, leaves the playhead alone', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 54), 42)).toBeNull();
  });

  it('the start dragged past the playhead drags the playhead with it', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 44, 47), 42)).toBe(44);
  });

  it('the start dragged earlier leaves the playhead alone', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 20, 47), 42)).toBeNull();
  });

  it('the start dragged later but still behind the playhead leaves the playhead alone', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 40, 47), 42)).toBeNull();
  });

  it('the start dragged exactly onto the playhead does not move it', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 42, 47), 42)).toBeNull();
  });

  it('bounds that did not move produce no seek', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 47), 42)).toBeNull();
  });

  it('a change to a segment that is not the selected one is ignored', () => {
    const before = region('r1', 37, 47);
    const after = region('r1', 37, 47);
    expect(computeEditSeek(before, after, 42)).toBeNull();
  });

  it('deselecting the looping segment never seeks', () => {
    expect(computeEditSeek(region('r1', 37, 47), null, 42)).toBeNull();
  });

  it('the selection moving to another segment never seeks', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r2', 10, 20), 42)).toBeNull();
  });

  it('the first evaluation (no previous bounds) only baselines', () => {
    expect(computeEditSeek(null, region('r1', 37, 47), 42)).toBeNull();
  });

  it('an edit to a segment the playhead had already left never pulls it in', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 44), 90)).toBeNull();
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 44, 47), 10)).toBeNull();
  });

  it('sliding the whole segment forward past the playhead does not move the playhead', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 43, 53), 42)).toBeNull();
  });

  it('sliding the whole segment behind the playhead closes the loop', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 20, 30), 42)).toBe(20);
  });

  it('a sub-millisecond bound wobble is not an edit', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37.00001, 47), 37.000005)).toBeNull();
  });
});
