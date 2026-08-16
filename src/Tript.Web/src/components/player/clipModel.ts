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

import type { ContentItem, CreateClipParameters, ClipSegment } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';

/** The two clipping modes, matching `CreateClipParameters.outputMode`. */
export type ClipMode = 'combine' | 'separate';

/** The fixed length of the default region proposed around the playbar cursor, seconds. */
export const DEFAULT_REGION_SECONDS = 10;

/**
 * How the default region is positioned: centred on the playbar cursor, keeping the full fixed
 * length. At the session edges the region is shifted inward (never shrunk, never negative) —
 * the user gets a full-length proposal they can then move/extend/shrink freely.
 */
export function buildDefaultRegion(
  cursorTime: number,
  duration: number,
  id: string,
  seconds = DEFAULT_REGION_SECONDS,
): TimelineRegion {
  const d = Math.max(0, duration);
  const length = Math.min(seconds, d);
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

/** Clamp a session time into [0, duration]. */
export function clampTime(time: number, duration: number): number {
  const d = Math.max(0, duration);
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

/** Two regions overlap when their open intervals intersect. */
export function overlaps(a: TimelineRegion, b: TimelineRegion): boolean {
  return a.start < b.end && b.start < a.end;
}

/** Remove a region by id. Returns the region list (never mutated). */
export function removeRegion(regions: TimelineRegion[], id: string): TimelineRegion[] {
  return regions.filter((region) => region.id !== id);
}

/** The marked spans in seconds order, deduplicated — the raw combine segments. */
export function regionsToSegments(regions: TimelineRegion[]): ClipSegment[] {
  return regions
    .slice()
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
 */
export function buildRegionClipPayload(params: {
  region: TimelineRegion;
  session: ContentItem;
  id: string;
  title: string;
  outputMode: ClipMode;
  audioTrackVolumes?: Record<string, number>;
  mutedAudioTracks?: string[];
}): CreateClipParameters {
  const { region, session, id, title, outputMode, audioTrackVolumes, mutedAudioTracks } = params;
  return {
    id,
    type: 'clip',
    game: null,
    igdbId: null,
    fileName: session.fileName,
    filePath: session.filePath,
    title,
    startTime: region.start,
    endTime: region.end,
    segments: [{ startTime: region.start, endTime: region.end }],
    outputMode,
    audioTrackVolumes,
    mutedAudioTracks,
  };
}

/**
 * The clip payload for combine mode — every marked region becomes one segment of a single clip.
 * The payload is shaped exactly like `CreateClipParameters`.
 */
export function buildCombineClipPayload(params: {
  regions: TimelineRegion[];
  session: ContentItem;
  id: string;
  title: string;
  audioTrackVolumes?: Record<string, number>;
  mutedAudioTracks?: string[];
}): CreateClipParameters {
  const { regions, session, id, title, audioTrackVolumes, mutedAudioTracks } = params;
  const segments = regionsToSegments(regions);
  return {
    id,
    type: 'clip',
    game: null,
    igdbId: null,
    fileName: session.fileName,
    filePath: session.filePath,
    title,
    startTime: segments.length > 0 ? segments[0].startTime : 0,
    endTime: segments.length > 0 ? segments[segments.length - 1].endTime : 0,
    segments,
    outputMode: 'combine',
    audioTrackVolumes,
    mutedAudioTracks,
  };
}
