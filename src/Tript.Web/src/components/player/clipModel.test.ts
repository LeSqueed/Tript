// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clipping domain model tests. The dialog logic is pure, so the default-region rule, the
// region-list bookkeeping, the combine/separate payload shapes and the units (all seconds) are
// pinned without any DOM.

import { describe, expect, it } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';
import {
  addRegion,
  buildCombineClipPayload,
  buildDefaultRegion,
  buildRegionClipPayload,
  isInsideRegion,
  overlaps,
  regionsToSegments,
  removeRegion,
} from './clipModel';

const session: ContentItem = {
  contentType: 'recording',
  fileName: 'session-1.mp4',
  filePath: 'sessions/2026-08-01/session-1.mp4',
  title: 'Session 1',
  startTime: 0,
  endTime: 100,
};

const region = (id: string, start: number, end: number): TimelineRegion => ({ id, start, end });

describe('buildDefaultRegion', () => {
  it('proposes a default region centred on the playbar cursor', () => {
    // currentTime 42, default length 10 → [37, 47].
    const r = buildDefaultRegion(42, 100, 'r1');
    expect(r).toEqual({ id: 'r1', start: 37, end: 47 });
  });

  it('clamps into the session at the start', () => {
    const r = buildDefaultRegion(2, 100, 'r1');
    expect(r.start).toBe(0);
    expect(r.end).toBe(10);
  });

  it('clamps into the session at the end', () => {
    const r = buildDefaultRegion(98, 100, 'r1');
    expect(r.start).toBe(90);
    expect(r.end).toBe(100);
  });

  it('never goes negative when the cursor is before the half length', () => {
    const r = buildDefaultRegion(0, 100, 'r1');
    expect(r.start).toBe(0);
    expect(r.end).toBe(10);
  });

  it('always has a positive length', () => {
    const r = buildDefaultRegion(50, 10, 'r1');
    expect(r.end - r.start).toBeGreaterThan(0);
  });
});

describe('addRegion / removeRegion', () => {
  it('appends a new region in insertion order', () => {
    const list = addRegion([], region('a', 10, 20));
    const next = addRegion(list, region('b', 40, 50));
    expect(next).toEqual([region('a', 10, 20), region('b', 40, 50)]);
  });

  it('replaces a region that already contains the new one (the user moved it)', () => {
    const list = [region('a', 10, 20), region('b', 40, 50)];
    const next = addRegion(list, region('a', 11, 19));
    expect(next).toHaveLength(2);
    expect(next[0]).toEqual({ id: 'a', start: 11, end: 19 });
  });

  it('removes overlap from an existing region so spans never double up', () => {
    const list = [region('a', 10, 30)];
    const next = addRegion(list, region('b', 20, 40));
    expect(next).toEqual([region('a', 10, 20), region('b', 20, 40)]);
  });

  it('removes a region by id', () => {
    const list = [region('a', 10, 20), region('b', 40, 50)];
    expect(removeRegion(list, 'b')).toEqual([region('a', 10, 20)]);
  });

  it('leaves the input list untouched', () => {
    const original = [region('a', 10, 30)];
    const next = addRegion(original, region('b', 20, 40));
    expect(original).toEqual([region('a', 10, 30)]);
    expect(next).not.toBe(original);
  });
});

describe('regionsToSegments', () => {
  it('orders regions by start time into segments in seconds', () => {
    const list = [region('a', 40, 50), region('b', 10, 20)];
    expect(regionsToSegments(list)).toEqual([
      { startTime: 10, endTime: 20 },
      { startTime: 40, endTime: 50 },
    ]);
  });
});

describe('combine vs separate payloads', () => {
  it('combine sends ONE CreateClip carrying every region as a segment', () => {
    const regions = [region('a', 10, 20), region('b', 40, 50)];
    const payload = buildCombineClipPayload({ regions, session, id: 'c1', title: 'My clip' });
    expect(payload.id).toBe('c1');
    expect(payload.type).toBe('clip');
    expect(payload.filePath).toBe('sessions/2026-08-01/session-1.mp4');
    expect(payload.title).toBe('My clip');
    expect(payload.outputMode).toBe('combine');
    expect(payload.segments).toEqual([
      { startTime: 10, endTime: 20 },
      { startTime: 40, endTime: 50 },
    ]);
    expect(payload.startTime).toBe(10);
    expect(payload.endTime).toBe(50);
  });

  it('separate builds ONE CreateClip per region, each a single segment', () => {
    const regions = [region('a', 10, 20), region('b', 40, 50)];
    const payloads = regions.map((r) =>
      buildRegionClipPayload({ region: r, session, id: `clip-${r.id}`, title: 'My clip', outputMode: 'separate' }),
    );
    expect(payloads).toHaveLength(2);
    expect(payloads[0]).toMatchObject({
      id: 'clip-a',
      outputMode: 'separate',
      startTime: 10,
      endTime: 20,
      segments: [{ startTime: 10, endTime: 20 }],
    });
    expect(payloads[1]).toMatchObject({
      id: 'clip-b',
      outputMode: 'separate',
      startTime: 40,
      endTime: 50,
      segments: [{ startTime: 40, endTime: 50 }],
    });
  });

  it('payloads are shaped exactly like CreateClipParameters', () => {
    const payload = buildCombineClipPayload({ regions: [region('a', 10, 20)], session, id: 'c1', title: 'X' });
    const keys = Object.keys(payload).sort();
    expect(keys).toEqual(
      [
        'id',
        'type',
        'game',
        'igdbId',
        'fileName',
        'filePath',
        'title',
        'startTime',
        'endTime',
        'segments',
        'outputMode',
        'audioTrackVolumes',
        'mutedAudioTracks',
      ].sort(),
    );
    // All times are in seconds.
    expect(payload.startTime).toBe(10);
    expect(payload.endTime).toBe(20);
    expect(payload.segments[0].startTime).toBe(10);
  });
});

describe('overlap / inside', () => {
  it('detects overlapping regions', () => {
    expect(overlaps(region('a', 10, 30), region('b', 20, 40))).toBe(true);
    expect(overlaps(region('a', 10, 20), region('b', 20, 40))).toBe(false);
    expect(overlaps(region('a', 10, 20), region('b', 40, 50))).toBe(false);
  });

  it('isInsideRegion uses the open interval', () => {
    const r = region('a', 10, 20);
    expect(isInsideRegion(r, 15)).toBe(true);
    expect(isInsideRegion(r, 10)).toBe(false);
    expect(isInsideRegion(r, 20)).toBe(false);
    expect(isInsideRegion(r, 5)).toBe(false);
  });
});
