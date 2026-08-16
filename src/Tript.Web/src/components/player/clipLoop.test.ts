// SPDX-License-Identifier: GPL-2.0-or-later
//
// Segment looping tests. The decision is pure: while playing inside the selected marked segment,
// crossing its end as a small step loops back to its start; leaving the segment (or inside none)
// runs normally.

import { describe, expect, it } from 'vitest';
import type { TimelineRegion } from './clipSeam';
import { computeLoopDecision } from './clipLoop';

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
    // A seek from far away landing exactly on the boundary is a jump, not a play-through.
    const decision = computeLoopDecision(50, 10, true, [region('r1', 10, 50)], 'r1');
    expect(decision.shouldLoopBack).toBe(false);
  });
});
