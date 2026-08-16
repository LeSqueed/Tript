// SPDX-License-Identifier: GPL-2.0-or-later
//
// Segment looping — the focused-review affordance from spec/frontend.md:
//
//   "Segments the user has created are clickable. Clicking a segment repeats just that segment
//    (loops it) for as long as the playhead is inside it. This is a focused review affordance:
//    stay inside a marked segment and it loops; leave it and normal playback resumes."
//
// The timeline surfaces `onRegionSelect` for the click; this module decides the loop. The rule,
// expressed purely so it can be unit-tested:
//
//   While playing, when the playhead CROSSES the end of the selected marked segment (the previous
//   sample was before the end, this sample is at/past it) as a small natural step, seek back to
//   that segment's start — the loop repeats just that segment. When the playhead is not inside the
//   segment, or the crossing was a large jump (a deliberate seek past the end — "leaving" the
//   segment), playback runs normally.
//
// The previous sample is needed because the <video> element drives currentTime through its own
// timeupdate events, which are not guaranteed to land exactly on the boundary. The step-size guard
// distinguishes a natural play-through (a few frames) from a deliberate seek past the end.

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
