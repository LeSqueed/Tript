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
//
// Selecting a segment is the other half of the affordance: the click both highlights the segment and
// puts the playhead on its first frame, so the loop starts at the top instead of wherever playback
// happened to be. That seek belongs to the selection handler (PlayerView), not here.
//
// A LOOPING SEGMENT IS ALSO BEING EDITED. While a segment loops, the user keeps shaping it — dragging
// an edge on the timeline, typing a bound in the dialog. That is a different trigger from the one
// above: the *bounds* move while the playhead may be perfectly stationary (even paused), so it cannot
// be read off a playhead sample without mistaking an edit for a seek. `computeEditSeek` is that
// second rule set, kept pure for the same reason: the decision is testable without React or a
// <video> element.

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
 * The smallest bound movement, in seconds, that counts as an edit at all.
 *
 * Bounds are recomputed from pointer pixels and from typed text, so "unchanged" has to tolerate the
 * last bits of a float. This is far below one frame (4ms even at 240fps), so no edit a user can
 * actually make is swallowed by it, and it doubles as the tolerance for recognising a whole-segment
 * slide (both edges moved by the same delta).
 */
export const LOOP_EDIT_EPSILON_SECONDS = 1e-4;

/**
 * Where the playhead must go after the *looping* segment's bounds changed, or null to leave it alone.
 *
 * Both arguments are the selected (looping) segment: `previous` as it was when this was last
 * evaluated, `next` as it is now. Passing anything else — a different segment, no selection — is what
 * makes the answer "no seek", so an edit to some other marked segment, a deselect, or a selection
 * moving to another segment can never move the playhead.
 *
 * The rules, in the order they are applied. All of them are conditional on the playhead having been
 * *in* the loop before the edit (closed interval — the loop-back seek parks the playhead exactly on
 * the start and a crossing sample lands exactly on the end; both are "in the loop"). Outside it the
 * user has left the segment and normal playback owns the playhead — nudging a distant segment's edge
 * must not teleport them into it.
 *
 *   1. The END moved and the playhead is now at or past it. The playhead is outside the loop with the
 *      whole rest of the session ahead of it, and the sample path cannot rescue it: that path fires
 *      only when the playhead moves *through* the end, and here the end moved through the playhead.
 *      So close the loop now — seek to the start.
 *   2. The START moved later and the playhead is now before it. The playhead would sit outside the
 *      segment it is reviewing, so it comes along with the edit — seek to the new start.
 *   3. Anything else: no seek. In particular a start moved *earlier* while the playhead is still
 *      inside leaves the playhead alone — the user is extending the segment backwards while watching
 *      it, and yanking the playhead back to the new in point would fight them.
 *
 * A whole-segment slide (both edges moved by the same delta, so the marked length survived) is not a
 * start edit and is excluded from rule 2: sliding a segment forward past the playhead is a "put it
 * over there" gesture, and playback simply runs into the segment and starts looping when it arrives.
 * Rule 1 still applies to a slide, because sliding a segment *backwards* past the playhead does leave
 * the loop unreachable.
 *
 * Deliberately independent of `playing`: unlike the sample path (where a crossing only means
 * something while the video is running), these rules keep the playhead inside the segment the user is
 * shaping, which matters just as much paused — the frame on screen should be a frame of the segment.
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
