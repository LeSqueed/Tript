// SPDX-License-Identifier: GPL-2.0-or-later
//
// Segment looping: clicking a segment repeats just that segment for as long as the playhead is
// inside it.

import type { TimelineRegion } from './clipSeam';

/**
 * The largest playhead step, in seconds, that still counts as a natural play-through rather than a
 * deliberate seek. Video timeupdate fires a few times per second, so a natural step is well under a
 * second; a deliberate seek is typically several seconds.
 */
export const LOOP_CROSS_STEP_SECONDS = 2;

export interface LoopDecision {
  /** The region the playhead is inside or whose end it crossed, if any. */
  region: TimelineRegion | null;
  /** True when playback must seek to `region.start` (the loop closed). */
  shouldLoopBack: boolean;
}

/**
 * Compute the loop decision for one playhead sample.
 *
 * @param currentTime  the playhead position, seconds
 * @param previousTime the playhead position at the previous sample, seconds
 * @param playing      whether the video is actually playing (seeking while paused is wrong)
 * @param regions      the marked regions, in insertion order
 * @param selectedRegionId the region the user clicked, if any — only the clicked segment loops
 */
export function computeLoopDecision(
  currentTime: number,
  previousTime: number,
  playing: boolean,
  regions: TimelineRegion[],
  selectedRegionId: string | null,
): LoopDecision {
  if (!playing || selectedRegionId === null) {
    return { region: null, shouldLoopBack: false };
  }
  const selected = regions.find((region) => region.id === selectedRegionId);
  if (!selected) {
    return { region: null, shouldLoopBack: false };
  }
  // The playhead crossed the selected segment's end since the last sample — the loop closes when
  // the crossing was a small natural step (a play-through), not a large seek past the end.
  const crossedEnd =
    previousTime < selected.end &&
    currentTime >= selected.end &&
    currentTime - previousTime < LOOP_CROSS_STEP_SECONDS;
  const inside = currentTime > selected.start && currentTime < selected.end;
  if (!crossedEnd && !inside) {
    return { region: null, shouldLoopBack: false };
  }
  return { region: selected, shouldLoopBack: crossedEnd };
}

/**
 * The smallest bound movement, in seconds, that counts as an edit at all. Bounds are recomputed
 * from pointer pixels and from typed text, so "unchanged" has to tolerate the last bits of a float.
 */
export const LOOP_EDIT_EPSILON_SECONDS = 1e-4;

/**
 * Where the playhead must go after the *looping* segment's bounds changed, or null to leave it
 * alone. Both arguments are the selected (looping) segment: `previous` as it was when this was last
 * evaluated, `next` as it is now.
 */
export function computeEditSeek(
  previous: TimelineRegion | null,
  next: TimelineRegion | null,
  currentTime: number,
): number | null {
  if (!previous || !next || previous.id !== next.id) {
    return null;
  }
  const startDelta = next.start - previous.start;
  const endDelta = next.end - previous.end;
  const startMoved = Math.abs(startDelta) > LOOP_EDIT_EPSILON_SECONDS;
  const endMoved = Math.abs(endDelta) > LOOP_EDIT_EPSILON_SECONDS;
  if (!startMoved && !endMoved) {
    return null;
  }
  const wasInLoop = currentTime >= previous.start && currentTime <= previous.end;
  if (!wasInLoop) {
    return null;
  }
  // Rule 1 — the end came back past the playhead (or the whole segment slid behind it).
  if (endMoved && currentTime >= next.end) {
    return next.start;
  }
  // A slide preserves the marked length; that is a move, not an edit of the start.
  const slid = startMoved && endMoved && Math.abs(startDelta - endDelta) <= LOOP_EDIT_EPSILON_SECONDS;
  // Rule 2 — the in point moved past the playhead, so the playhead follows it.
  if (!slid && startDelta > LOOP_EDIT_EPSILON_SECONDS && currentTime < next.start) {
    return next.start;
  }
  // Rule 3 — the playhead is still inside the edited segment: leave it where the user put it.
  return null;
}
