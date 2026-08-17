// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clipping domain model — pure functions behind the clip dialog (T9).
//
// A region is a start + an end on the session timeline, in seconds, independent of bookmarks
// (spec/recorder.md — "Clipping from a session — the region model"). The dialog collects the
// user's marked regions and turns them into the `segments` list a `CreateClip` carries. The two
// modes differ only in how the regions are grouped into payloads:
//
//   - combine  — every region becomes one segment of a single clip (simple concatenation).
//   - separate — each region becomes its own clip; a region IS a one-segment clip.
//
// All the functions here are deterministic so the dialog logic can be unit-tested without a DOM.
//
// THE BOUNDS INVARIANT. A region that reaches CreateClip must satisfy
// `0 <= start < end <= duration`, with `end - start >= MIN_REGION_SECONDS`. Every helper below is
// written so that it either returns a region satisfying that invariant, or returns its input
// untouched (an edit it cannot make safely is refused, never approximated). The payload builders are
// stricter still: they have no "refuse" option, so they clamp and *drop* — a segment that cannot be
// made to fit is not sent.
//
// The invariant is only as good as the duration it is checked against, which is the subtle part:
// `duration` here is a *measurement of the media*, not a guess about it. `PlayerView` used to clamp
// against the player's fallback length (DEFAULT_SESSION_SECONDS = 120s, a fabricated placeholder
// used until the <video> element reports its metadata), which meant a segment could be marked out to
// 0:45 on a file that was really 8s long and every clamp would allow it — the clamping was correct
// and the number was a lie. Hence `resolveClipBounds` (which duration is authoritative),
// `markableDuration` (when there is no authoritative one at all, and so nothing may be marked) and
// `reconcileRegion` (what happens to regions when the authoritative duration turns out to be
// shorter than what they were clamped against).
//
// The metadata record's declared length is not a lesser measurement, it is not a measurement: it was
// observed declaring 100s for a file that was really 9.13s. It lays out a timeline; it never bounds a
// segment.

import type { ContentItem, CreateClipParameters, ClipSegment } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';

/** The two clipping modes, matching `CreateClipParameters.outputMode`. */
export type ClipMode = 'combine' | 'separate';

/** The fixed length of the default region proposed around the playbar cursor, seconds. */
export const DEFAULT_REGION_SECONDS = 10;

/**
 * The shortest region a mark, a drag or a typed edit may produce, seconds.
 *
 * Every editing path funnels through the helpers below, so this is the single place that decides a
 * region can never collapse: dragging an edge past the opposite one, or typing an end before the
 * start, stops here instead of producing a zero-length (or inverted) span the backend would have to
 * reject. It is deliberately small — the user is trimming frames, not shapes.
 */
export const MIN_REGION_SECONDS = 0.25;

/**
 * The clippable length of the media and where that number came from.
 *
 * `known` is true only when the media itself reported the duration. It is not a UI nicety: a bound
 * nobody measured is not a bound. When it is false, `seconds` is at best a *declared* length (the
 * content record's) and at worst 0 — good enough to lay out a timeline, never good enough to place a
 * segment inside. `markableDuration` below is the only sanctioned way to turn these two fields into
 * a bound, because reading `seconds` on its own is exactly the mistake that produced the bug it
 * documents.
 */
export interface ClipBounds {
  /** The end of the clippable range, seconds. 0 when nothing authoritative is known yet. */
  seconds: number;
  /** Whether the media itself vouched for `seconds` (as opposed to a metadata record). */
  known: boolean;
}

/**
 * Decide which duration segments are clamped against.
 *
 * The media's own duration wins whenever it is available. Metadata (`ContentItem.endTime`) is
 * written by a different code path than the file — a recording cut short by a crash, a record
 * imported from elsewhere, a re-encode — so the two can disagree; the file is the thing ffmpeg will
 * actually cut, so the file is authoritative and metadata is only a stand-in until it loads.
 *
 * When neither is available the answer is 0, deliberately: the player's fallback session length is a
 * placeholder constant, and clamping against it is what let out-of-bounds segments exist in the first
 * place. Refusing to mark for the moment it takes the media to load is the honest alternative.
 *
 * Note what "stand-in" does and does not license. The metadata length is a stand-in for *drawing* —
 * a timeline has to be laid out against something — and never for bounding a segment; `known: false`
 * is how this function says so. See `markableDuration`.
 */
export function resolveClipBounds(
  mediaDuration: number | undefined,
  metadataDuration: number | undefined,
): ClipBounds {
  if (mediaDuration !== undefined && Number.isFinite(mediaDuration) && mediaDuration > 0) {
    return { seconds: mediaDuration, known: true };
  }
  if (metadataDuration !== undefined && Number.isFinite(metadataDuration) && metadataDuration > 0) {
    return { seconds: metadataDuration, known: false };
  }
  return { seconds: 0, known: false };
}

/**
 * The bound a segment may actually be created against: the measured length, or 0 (nothing markable)
 * when nothing has been measured.
 *
 * MEASURED BUG (this function is the fix, and its whole reason to exist). A session whose content
 * record declared 100s pointed at a file that was really 9.13s long. With the <video> element not yet
 * having reported its metadata, `resolveClipBounds` answered `{ seconds: 100, known: false }` — the
 * declared length, flagged as a guess — and every caller then read `.seconds` and clamped against it:
 * `Mark 10s` at 0:95 produced a segment ending at 1:40, some 91 seconds past the last frame that
 * exists. `known` had been there from the start and said the number was unmeasured; nothing anywhere
 * read it. A later reconciliation pass truncated the segment once the media loaded, but the mark was
 * wrong the moment it was made, and "a later pass will clean it up" is not a bound.
 *
 * The declared length cannot be repaired into a bound, only replaced by one: it can overstate the
 * media (a record written by a different code path than the file, a recording a crash cut short, an
 * imported or re-encoded record), and there is no second measurement to take the minimum against.
 * So the conversion from `ClipBounds` to a number lives here, in one place, and refuses an unmeasured
 * one — the alternative is every call site remembering to check a flag, which is what failed.
 */
export function markableDuration(bounds: ClipBounds): number {
  return bounds.known ? bounds.seconds : 0;
}

/**
 * The duration as a usable clamping bound, or null when there is none.
 *
 * A duration that is not a finite number, or is too short to hold the shortest allowed region, cannot
 * bound anything: NaN comparisons are all false (so `time > d` would not stop anything) and Infinity
 * bounds nothing at all. Both used to reach the clamps — `useClipDialog` passed
 * `session.endTime ?? Infinity`, i.e. "unbounded" for any recording without a metadata record.
 */
function clippableDuration(duration: number): number | null {
  return Number.isFinite(duration) && duration >= MIN_REGION_SECONDS ? duration : null;
}

/**
 * Bring one region inside [0, duration], or drop it (null) when nothing usable survives.
 *
 * This is the reconciliation rule, applied both when the authoritative duration lands (a region
 * marked against a longer, provisional duration must not survive as an out-of-bounds one) and
 * defensively inside every editing helper below, so a stale oversized region cannot be edited into a
 * still-oversized one:
 *
 *   - a region straddling the real end is truncated to it, provided MIN_REGION_SECONDS survive;
 *   - a region lying entirely at or beyond the real end is dropped, not squashed into a sliver at
 *     the end — the user marked something that does not exist, and a 0.25s clip of the last frame is
 *     not a better answer than no clip;
 *   - anything non-finite is dropped.
 *
 * A region already inside the bounds is returned by identity, which keeps `reconcileRegions` cheap
 * enough to run on every duration change.
 */
export function reconcileRegion(region: TimelineRegion, duration: number): TimelineRegion | null {
  const d = clippableDuration(duration);
  if (d === null || !Number.isFinite(region.start) || !Number.isFinite(region.end)) {
    return null;
  }
  const lo = Math.max(0, Math.min(region.start, region.end));
  const hi = Math.min(Math.max(region.start, region.end), d);
  if (hi - lo < MIN_REGION_SECONDS) {
    return null;
  }
  return lo === region.start && hi === region.end ? region : { ...region, start: lo, end: hi };
}

/**
 * Reconcile a whole region list against the duration, dropping what cannot fit.
 *
 * Returns the input array by identity when every region already fits, so it is safe to call from an
 * effect keyed on the duration without looping.
 */
export function reconcileRegions(regions: TimelineRegion[], duration: number): TimelineRegion[] {
  const next: TimelineRegion[] = [];
  let changed = false;
  for (const region of regions) {
    const fitted = reconcileRegion(region, duration);
    if (fitted === null) {
      changed = true;
      continue;
    }
    if (fitted !== region) {
      changed = true;
    }
    next.push(fitted);
  }
  return changed ? next : regions;
}

/**
 * How the default region is positioned: centred on the playbar cursor, keeping the full fixed
 * length. At the session edges the region is shifted inward (never shrunk, never negative) —
 * the user gets a full-length proposal they can then move/extend/shrink freely. On media shorter
 * than the fixed length the proposal shrinks to the whole media instead.
 *
 * With no usable duration (nothing measured yet) the result is the empty region at 0: callers commit
 * it through `markRegion`/`normalizeRegionBounds`, which refuse a span below MIN_REGION_SECONDS, so
 * "no duration" produces no segment rather than a fabricated one.
 */
export function buildDefaultRegion(
  cursorTime: number,
  duration: number,
  id: string,
  seconds = DEFAULT_REGION_SECONDS,
): TimelineRegion {
  const d = clippableDuration(duration) ?? 0;
  const length = Math.min(Number.isFinite(seconds) ? Math.max(0, seconds) : 0, d);
  const half = length / 2;
  const center = clampTime(cursorTime, d);
  let start = center - half;
  if (start < 0) {
    start = 0;
  }
  if (start + length > d) {
    start = Math.max(0, d - length);
  }
  return { id, start, end: start + length };
}

/** A fresh region id. V4 form is enough for stable React keys and clip ids. */
export function newRegionId(prefix = 'region'): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/** A fresh clip id, namespaced so in-flight progress can be correlated to a CreateClip. */
export function newClipId(prefix = 'clip'): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

/**
 * Clamp a session time into [0, duration].
 *
 * Non-finite inputs collapse to 0 rather than propagating: a NaN duration used to make every
 * comparison downstream false (NaN is neither greater nor smaller than anything), which turns a clamp
 * into a pass-through, and an infinite duration bounds nothing.
 */
export function clampTime(time: number, duration: number): number {
  const d = Number.isFinite(duration) ? Math.max(0, duration) : 0;
  if (!Number.isFinite(time)) {
    return 0;
  }
  return Math.min(Math.max(0, time), d);
}

/**
 * Add a region to the list. Regions keep insertion order (their display order); a region already
 * containing the new one is replaced (the user "moved" it), otherwise the region is appended and
 * overlap with an existing region is removed from the old one so the marked spans never double up
 * in the concatenated output.
 */
export function addRegion(
  regions: TimelineRegion[],
  region: TimelineRegion,
): TimelineRegion[] {
  const next = [...regions];
  // A region already containing the new one is replaced (the user refined/moved it).
  const containing = next.findIndex((existing) =>
    existing.start <= region.start && existing.end >= region.end,
  );
  if (containing >= 0) {
    next[containing] = region;
    return next;
  }
  // Partial overlap is trimmed out of the existing region so the marked spans never double up in
  // the concatenated output. A region the new one fully covers drops away entirely.
  const trimmed = next
    .flatMap((existing) => {
      if (!overlaps(existing, region)) {
        return [existing];
      }
      const pieces: TimelineRegion[] = [];
      if (existing.start < region.start) {
        pieces.push({ ...existing, end: region.start });
      }
      if (existing.end > region.end) {
        pieces.push({ ...existing, start: region.end });
      }
      return pieces;
    })
    .filter((piece) => piece.end > piece.start);
  return [...trimmed, region];
}

/**
 * Order + clamp raw bounds into a usable span, or null when they cannot make a region.
 *
 * This is the gate every *edit* goes through (the numeric fields in the dialog, the set-from-playhead
 * buttons, the timeline drag, and defensively the controller itself), so a typed edit and a dragged
 * edge can never disagree about what the resulting region is. Bounds are ordered (an end typed
 * before the start is simply the other way round), clamped into the session, and a span shorter than
 * MIN_REGION_SECONDS is refused rather than silently rounded up — the caller keeps the old region.
 *
 * With no usable duration to clamp against, every edit is refused: there is no honest bound to place
 * the region inside, and inventing one is exactly the bug this module is guarding against.
 */
export function normalizeRegionBounds(
  start: number,
  end: number,
  duration: number,
): { start: number; end: number } | null {
  const d = clippableDuration(duration);
  if (d === null || !Number.isFinite(start) || !Number.isFinite(end)) {
    return null;
  }
  const lo = clampTime(Math.min(start, end), d);
  const hi = clampTime(Math.max(start, end), d);
  if (hi - lo < MIN_REGION_SECONDS) {
    return null;
  }
  return { start: lo, end: hi };
}

/**
 * Move a whole region by a signed delta, preserving its length.
 *
 * Dragging a region's body at the session edges clamps the *whole* region rather than squashing it:
 * the length the user marked survives the gesture, which is what makes body-dragging feel like
 * sliding a card instead of resizing one.
 *
 * The length that survives is the *reconciled* length: a region longer than the media (one marked
 * against a longer provisional duration) is truncated first, otherwise preserving its length would
 * mean sliding an oversized region around inside a shorter media and reporting an end past its last
 * frame.
 */
export function moveRegionBy(
  region: TimelineRegion,
  deltaSeconds: number,
  duration: number,
): TimelineRegion {
  const d = clippableDuration(duration);
  const base = d === null ? null : reconcileRegion(region, d);
  if (d === null || base === null || !Number.isFinite(deltaSeconds)) {
    return region;
  }
  const length = Math.max(0, base.end - base.start);
  const start = Math.min(Math.max(0, base.start + deltaSeconds), Math.max(0, d - length));
  return { ...base, start, end: start + length };
}

/**
 * Set a region's start (its left edge / in point) to a session time.
 *
 * Clamped into the session and stopped MIN_REGION_SECONDS short of the end, so dragging the left
 * edge to the right never crosses the right edge (it parks against it instead — the standard NLE
 * feel, and it keeps the region reversible: drag back and the span reopens).
 *
 * The *other* edge is clamped too, via `reconcileRegion`. That is not belt-and-braces: the ceiling is
 * derived from `region.end`, so a region carried over from a longer, provisional duration (end past
 * the real end of the media) used to have its start clamped correctly while its oversized end was
 * copied straight through — the one input that made this function emit `end > duration`.
 */
export function resizeRegionStart(
  region: TimelineRegion,
  time: number,
  duration: number,
): TimelineRegion {
  const d = clippableDuration(duration);
  const base = d === null ? null : reconcileRegion(region, d);
  if (d === null || base === null || !Number.isFinite(time)) {
    return region;
  }
  const ceiling = base.end - MIN_REGION_SECONDS;
  if (ceiling < 0) {
    return base;
  }
  return { ...base, start: Math.min(clampTime(time, d), ceiling) };
}

/**
 * Set a region's end (its right edge / out point) to a session time. The mirror of
 * `resizeRegionStart`: clamped into the session, and never nearer than MIN_REGION_SECONDS to the
 * start. A region whose start sits within MIN_REGION_SECONDS of the session end cannot be resized
 * at all (there is no room), so it is returned untouched.
 *
 * The `Math.max(..., floor)` below re-raises the end *after* the clamp, so it is only safe because
 * `floor > d` returns early: `floor` can exceed the duration when the start sits within
 * MIN_REGION_SECONDS of the end. `reconcileRegion` closes the other way in — a start that is itself
 * at or beyond the duration (a stale region from a longer provisional duration) can no longer reach
 * that arithmetic at all.
 */
export function resizeRegionEnd(
  region: TimelineRegion,
  time: number,
  duration: number,
): TimelineRegion {
  const d = clippableDuration(duration);
  const base = d === null ? null : reconcileRegion(region, d);
  if (d === null || base === null || !Number.isFinite(time)) {
    return region;
  }
  const floor = base.start + MIN_REGION_SECONDS;
  if (floor > d) {
    return base;
  }
  return { ...base, end: Math.max(clampTime(time, d), floor) };
}

/** Two regions overlap when their open intervals intersect. */
export function overlaps(a: TimelineRegion, b: TimelineRegion): boolean {
  return a.start < b.end && b.start < a.end;
}

/** Remove a region by id. Returns the region list (never mutated). */
export function removeRegion(regions: TimelineRegion[], id: string): TimelineRegion[] {
  return regions.filter((region) => region.id !== id);
}

/**
 * The marked spans in seconds order — the raw combine segments.
 *
 * The last gate before the wire. Regions arrive here having been clamped by whichever edit produced
 * them, but "clamped" is only as true as the duration in force at the time, and regions outlive the
 * moment they were marked in (closing the clip dialog deliberately keeps them). So the duration is
 * applied once more here, and a segment that cannot be made to fit is dropped rather than sent: the
 * backend receives no segment outside [0, duration], whatever happened upstream.
 */
export function regionsToSegments(regions: TimelineRegion[], duration: number): ClipSegment[] {
  return regions
    .map((region) => reconcileRegion(region, duration))
    .filter((region): region is TimelineRegion => region !== null)
    .sort((a, b) => a.start - b.start)
    .map(({ start, end }) => ({ startTime: start, endTime: end }));
}

/** Whether a given time sits inside a region (open interval, exclusive ends). */
export function isInsideRegion(region: TimelineRegion, time: number): boolean {
  return time > region.start && time < region.end;
}

/**
 * The clip payload for one marked region — a one-segment clip (the "separate" unit).
 * The payload is shaped exactly like `CreateClipParameters`, so a sent payload is
 * the whole contract, not a partial.
 *
 * `duration` is the media length the segment is validated against (see `regionsToSegments`). A region
 * that does not fit inside it leaves no clip to make, and the answer is null — no payload — rather
 * than a payload carrying bounds the file cannot honour.
 */
export function buildRegionClipPayload(params: {
  region: TimelineRegion;
  session: ContentItem;
  duration: number;
  id: string;
  title: string;
  outputMode: ClipMode;
  audioTrackVolumes?: Record<string, number>;
  mutedAudioTracks?: string[];
}): CreateClipParameters | null {
  const { region, session, duration, id, title, outputMode, audioTrackVolumes, mutedAudioTracks } =
    params;
  const segments = regionsToSegments([region], duration);
  if (segments.length === 0) {
    return null;
  }
  return {
    id,
    type: 'clip',
    game: null,
    igdbId: null,
    fileName: session.fileName,
    filePath: session.filePath,
    title,
    startTime: segments[0].startTime,
    endTime: segments[0].endTime,
    segments,
    outputMode,
    audioTrackVolumes,
    mutedAudioTracks,
  };
}

/**
 * The clip payload for combine mode — every marked region becomes one segment of a single clip.
 * The payload is shaped exactly like `CreateClipParameters`.
 *
 * Regions that do not fit inside `duration` are dropped (see `regionsToSegments`); when none survive
 * there is nothing to concatenate and the answer is null rather than a segment-less CreateClip.
 */
export function buildCombineClipPayload(params: {
  regions: TimelineRegion[];
  session: ContentItem;
  duration: number;
  id: string;
  title: string;
  audioTrackVolumes?: Record<string, number>;
  mutedAudioTracks?: string[];
}): CreateClipParameters | null {
  const { regions, session, duration, id, title, audioTrackVolumes, mutedAudioTracks } = params;
  const segments = regionsToSegments(regions, duration);
  if (segments.length === 0) {
    return null;
  }
  return {
    id,
    type: 'clip',
    game: null,
    igdbId: null,
    fileName: session.fileName,
    filePath: session.filePath,
    title,
    startTime: segments[0].startTime,
    endTime: segments[segments.length - 1].endTime,
    segments,
    outputMode: 'combine',
    audioTrackVolumes,
    mutedAudioTracks,
  };
}
