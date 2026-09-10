// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem, CreateClipParameters, ClipSegment } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';

export type ClipMode = 'combine' | 'separate';

export const DEFAULT_REGION_SECONDS = 10;

export const MIN_REGION_SECONDS = 0.25;

export interface ClipBounds {
  seconds: number;
  known: boolean;
}

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

export function markableDuration(bounds: ClipBounds): number {
  return bounds.known ? bounds.seconds : 0;
}

function clippableDuration(duration: number): number | null {
  return Number.isFinite(duration) && duration >= MIN_REGION_SECONDS ? duration : null;
}

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

export function newRegionId(prefix = 'region'): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export function newClipId(prefix = 'clip'): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export function clampTime(time: number, duration: number): number {
  const d = Number.isFinite(duration) ? Math.max(0, duration) : 0;
  if (!Number.isFinite(time)) {
    return 0;
  }
  return Math.min(Math.max(0, time), d);
}

export function addRegion(
  regions: TimelineRegion[],
  region: TimelineRegion,
): TimelineRegion[] {
  const next = [...regions];
  const containing = next.findIndex((existing) =>
    existing.start <= region.start && existing.end >= region.end,
  );
  if (containing >= 0) {
    next[containing] = region;
    return next;
  }
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

export function overlaps(a: TimelineRegion, b: TimelineRegion): boolean {
  return a.start < b.end && b.start < a.end;
}

export function removeRegion(regions: TimelineRegion[], id: string): TimelineRegion[] {
  return regions.filter((region) => region.id !== id);
}

export function regionsToSegments(regions: TimelineRegion[], duration: number): ClipSegment[] {
  return regions
    .map((region) => reconcileRegion(region, duration))
    .filter((region): region is TimelineRegion => region !== null)
    .map(({ start, end }) => ({ startTime: start, endTime: end }));
}

export function isInsideRegion(region: TimelineRegion, time: number): boolean {
  return time > region.start && time < region.end;
}

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
