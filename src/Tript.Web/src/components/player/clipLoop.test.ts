// SPDX-License-Identifier: GPL-2.0-or-later
//
// Segment looping tests. Both decisions are pure.
//
// The sample path: while playing inside the selected marked segment, crossing its end as a small step
// loops back to its start; leaving the segment (or inside none) runs normally.
//
// The edit path: while a segment loops, the user keeps shaping it. The end takes effect immediately
// (dragged behind the playhead, the loop closes there and then), a start pushed past the playhead
// brings the playhead with it, and a start pulled back leaves the playhead where the user is watching.

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
    // A seek from far away landing exactly on the boundary is a jump, not a play-through.
    const decision = computeLoopDecision(50, 10, true, [region('r1', 10, 50)], 'r1');
    expect(decision.shouldLoopBack).toBe(false);
  });
});

// Editing the looping segment. The playhead does not move here — the *bounds* do, which is why this
// is a separate rule set from the sample path above.
describe('computeEditSeek', () => {
  it('the end dragged before the playhead loops back to the start', () => {
    // Playhead at 46 inside [37, 47]; the out point comes back to 44, leaving the playhead past the
    // loop's end. No crossing will ever be sampled (the end moved through the playhead, not the other
    // way round), so the loop has to close here.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 44), 46)).toBe(37);
  });

  it('the end dragged later, still ahead of the playhead, leaves the playhead alone', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 54), 42)).toBeNull();
  });

  it('the start dragged past the playhead drags the playhead with it', () => {
    // Playhead at 42 inside [37, 47]; the in point moves to 44, so the playhead would sit outside.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 44, 47), 42)).toBe(44);
  });

  it('the start dragged earlier leaves the playhead alone', () => {
    // Extending the segment backwards while watching it: the playhead is still inside, so it stays.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 20, 47), 42)).toBeNull();
  });

  it('the start dragged later but still behind the playhead leaves the playhead alone', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 40, 47), 42)).toBeNull();
  });

  it('the start dragged exactly onto the playhead does not move it', () => {
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 42, 47), 42)).toBeNull();
  });

  it('bounds that did not move produce no seek', () => {
    // Every render re-evaluates the rule; an unchanged selection must be inert.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 47), 42)).toBeNull();
  });

  it('a change to a segment that is not the selected one is ignored', () => {
    // The caller only ever passes the *selected* segment, so an edit to any other one shows up here
    // as "the selected segment did not move".
    const before = region('r1', 37, 47);
    const after = region('r1', 37, 47);
    expect(computeEditSeek(before, after, 42)).toBeNull();
  });

  it('deselecting the looping segment never seeks', () => {
    expect(computeEditSeek(region('r1', 37, 47), null, 42)).toBeNull();
  });

  it('the selection moving to another segment never seeks', () => {
    // Different ids: the bounds of two different segments are not an edit, however far apart they are.
    expect(computeEditSeek(region('r1', 37, 47), region('r2', 10, 20), 42)).toBeNull();
  });

  it('the first evaluation (no previous bounds) only baselines', () => {
    expect(computeEditSeek(null, region('r1', 37, 47), 42)).toBeNull();
  });

  it('an edit to a segment the playhead had already left never pulls it in', () => {
    // The user seeked away (normal playback resumed) and then tweaked the segment's end. Teleporting
    // them into a segment they are not watching would be worse than the loop staying dormant.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37, 44), 90)).toBeNull();
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 44, 47), 10)).toBeNull();
  });

  it('sliding the whole segment forward past the playhead does not move the playhead', () => {
    // A slide preserves the marked length — it is "put the segment over there", not an edit of the in
    // point. Playback runs into the segment and starts looping when it arrives.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 43, 53), 42)).toBeNull();
  });

  it('sliding the whole segment behind the playhead closes the loop', () => {
    // The playhead is past the end and the loop would otherwise be unreachable.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 20, 30), 42)).toBe(20);
  });

  it('a sub-millisecond bound wobble is not an edit', () => {
    // Bounds are recomputed from pointer pixels and typed text; the last bits of a float must not
    // register as a start edit. The playhead here sits between the two starts, so without the epsilon
    // this would seek.
    expect(computeEditSeek(region('r1', 37, 47), region('r1', 37.00001, 47), 37.000005)).toBeNull();
  });
});
