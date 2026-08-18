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
  clampTime,
  DEFAULT_REGION_SECONDS,
  isInsideRegion,
  markableDuration,
  reconcileRegion,
  reconcileRegions,
  resolveClipBounds,
  MIN_REGION_SECONDS,
  moveRegionBy,
  normalizeRegionBounds,
  overlaps,
  regionsToSegments,
  removeRegion,
  resizeRegionEnd,
  resizeRegionStart,
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

// The geometry behind every adjustment: the timeline drag (body / left edge / right edge), the typed
// bounds and the snap-to-playhead buttons all reduce to these three functions, so the clamping rules
// are pinned here once rather than through the DOM.

describe('moveRegionBy — dragging a region body', () => {
  it('slides the region and preserves its length', () => {
    expect(moveRegionBy(region('a', 30, 40), 5, 100)).toEqual(region('a', 35, 45));
    expect(moveRegionBy(region('a', 30, 40), -12, 100)).toEqual(region('a', 18, 28));
  });

  it('clamps the whole region at the session start instead of squashing it', () => {
    const moved = moveRegionBy(region('a', 5, 15), -20, 100);
    expect(moved).toEqual(region('a', 0, 10));
    expect(moved.end - moved.start).toBe(10);
  });

  it('clamps the whole region at the session end instead of squashing it', () => {
    const moved = moveRegionBy(region('a', 80, 90), 40, 100);
    expect(moved).toEqual(region('a', 90, 100));
    expect(moved.end - moved.start).toBe(10);
  });

  it('ignores a non-finite delta', () => {
    expect(moveRegionBy(region('a', 30, 40), Number.NaN, 100)).toEqual(region('a', 30, 40));
  });
});

describe('resizeRegionStart / resizeRegionEnd — dragging an edge', () => {
  it('moves only the dragged edge', () => {
    expect(resizeRegionStart(region('a', 30, 40), 25, 100)).toEqual(region('a', 25, 40));
    expect(resizeRegionEnd(region('a', 30, 40), 55, 100)).toEqual(region('a', 30, 55));
  });

  it('clamps the edges into the session', () => {
    expect(resizeRegionStart(region('a', 30, 40), -10, 100)).toEqual(region('a', 0, 40));
    expect(resizeRegionEnd(region('a', 30, 40), 500, 100)).toEqual(region('a', 30, 100));
  });

  it('parks the start against the end rather than inverting the region', () => {
    const resized = resizeRegionStart(region('a', 30, 40), 90, 100);
    expect(resized.start).toBe(40 - MIN_REGION_SECONDS);
    expect(resized.end).toBe(40);
    expect(resized.end - resized.start).toBe(MIN_REGION_SECONDS);
  });

  it('parks the end against the start rather than inverting the region', () => {
    const resized = resizeRegionEnd(region('a', 30, 40), 2, 100);
    expect(resized.start).toBe(30);
    expect(resized.end).toBe(30 + MIN_REGION_SECONDS);
  });

  it('leaves a region with no room at the session end untouched', () => {
    // The start already sits within the minimum length of the session end: nothing can be resized.
    expect(resizeRegionEnd(region('a', 99.9, 100), 50, 100)).toEqual(region('a', 99.9, 100));
  });

  it('ignores a non-finite time', () => {
    expect(resizeRegionStart(region('a', 30, 40), Number.NaN, 100)).toEqual(region('a', 30, 40));
    expect(resizeRegionEnd(region('a', 30, 40), Number.NaN, 100)).toEqual(region('a', 30, 40));
  });
});

describe('normalizeRegionBounds — the gate every edit goes through', () => {
  it('orders reversed bounds', () => {
    expect(normalizeRegionBounds(60, 20, 100)).toEqual({ start: 20, end: 60 });
  });

  it('clamps bounds outside the session', () => {
    expect(normalizeRegionBounds(-30, 500, 100)).toEqual({ start: 0, end: 100 });
  });

  it('refuses a span shorter than the minimum instead of producing a broken region', () => {
    expect(normalizeRegionBounds(40, 40, 100)).toBeNull();
    expect(normalizeRegionBounds(40, 40 + MIN_REGION_SECONDS / 2, 100)).toBeNull();
    expect(normalizeRegionBounds(40, 40 + MIN_REGION_SECONDS, 100)).toEqual({
      start: 40,
      end: 40 + MIN_REGION_SECONDS,
    });
  });

  it('refuses non-finite bounds', () => {
    expect(normalizeRegionBounds(Number.NaN, 40, 100)).toBeNull();
    expect(normalizeRegionBounds(10, Number.POSITIVE_INFINITY, 100)).toBeNull();
  });
});

describe('regionsToSegments', () => {
  it('orders regions by start time into segments in seconds', () => {
    const list = [region('a', 40, 50), region('b', 10, 20)];
    expect(regionsToSegments(list, 100)).toEqual([
      { startTime: 10, endTime: 20 },
      { startTime: 40, endTime: 50 },
    ]);
  });
});

describe('combine vs separate payloads', () => {
  it('combine sends ONE CreateClip carrying every region as a segment', () => {
    const regions = [region('a', 10, 20), region('b', 40, 50)];
    const payload = buildCombineClipPayload({ regions, session, duration: 100, id: 'c1', title: 'My clip' })!;
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
      buildRegionClipPayload({
        region: r,
        session,
        duration: 100,
        id: `clip-${r.id}`,
        title: 'My clip',
        outputMode: 'separate',
      }),
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
    const payload = buildCombineClipPayload({
      regions: [region('a', 10, 20)],
      session,
      duration: 100,
      id: 'c1',
      title: 'X',
    })!;
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

// ---------------------------------------------------------------------------------------------
// The bounds invariant, property-style.
//
// The user's requirement is a single sentence — a segment can never sit outside the clip length, at
// either end, including the default 10s one — so it is worth checking as a property over a range of
// durations and adversarial inputs rather than only at the handful of points the examples above pick.
//
// Two things are asserted, and the split matters. Every helper is allowed to *refuse* an edit (it
// returns its input untouched: a NaN time, no usable duration, a stale region with nowhere to go), so
// the general property is "changed ⇒ in bounds". Fed a region that was already in bounds — the state
// the rest of the module guarantees — the helpers must return something in bounds unconditionally.
// The payload builders get no refusal: they clamp and drop, so their segments are checked outright.

/** Durations worth checking: nothing, unusably short, shorter than the default 10s region, normal. */
const DURATIONS = [0, 0.1, 0.25, 1, 3, 8, 10, 100];

/** Times a caller can produce: outside both ends, on the boundaries, and non-finite. */
const TIMES = [
  -1000, -0.1, 0, 0.05, 0.25, 2.5, 7.9, 8, 9.99, 100, 1_000_000,
  Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY,
];

/**
 * Regions a caller can hold, including the ones the fallback-duration bug produced: spans marked
 * against a provisional 120s length that the real media does not reach.
 */
const ADVERSARIAL_REGIONS: TimelineRegion[] = [
  region('inside', 10, 20),
  region('at-zero', 0, MIN_REGION_SECONDS),
  region('negative-start', -50, 20),
  region('both-negative', -50, -10),
  region('inverted', 60, 20),
  region('zero-length', 30, 30),
  region('sliver', 30, 30.1),
  region('straddles-end', 90, 400),
  region('entirely-beyond', 200, 300),
  region('the-whole-placeholder', 0, 120),
  region('nan-start', Number.NaN, 40),
  region('infinite-end', 10, Number.POSITIVE_INFINITY),
];

/** Slack for the length comparison only: the parked bounds are computed by subtraction. */
const EPSILON = 1e-9;

function expectInBounds(subject: TimelineRegion, duration: number, label: string): void {
  const detail = `${label} → [${subject.start}, ${subject.end}] within [0, ${duration}]`;
  expect(Number.isFinite(subject.start), detail).toBe(true);
  expect(Number.isFinite(subject.end), detail).toBe(true);
  expect(subject.start, detail).toBeGreaterThanOrEqual(0);
  expect(subject.end, detail).toBeLessThanOrEqual(duration);
  expect(subject.end - subject.start, detail).toBeGreaterThanOrEqual(MIN_REGION_SECONDS - EPSILON);
}

/** Whether the helper refused the edit and handed the caller's own region back. */
function unchanged(before: TimelineRegion, after: TimelineRegion): boolean {
  return (
    Object.is(before.start, after.start) &&
    Object.is(before.end, after.end) &&
    before.id === after.id
  );
}

/** The regions that are legitimately inside a given duration — what the module guarantees it holds. */
function inBoundsRegionsFor(duration: number): TimelineRegion[] {
  return [
    region('whole', 0, duration),
    region('head', 0, MIN_REGION_SECONDS),
    region('tail', duration - MIN_REGION_SECONDS, duration),
    region('middle', duration / 4, duration / 2),
  ].filter(
    (candidate) =>
      candidate.start >= 0 &&
      candidate.end <= duration &&
      candidate.end - candidate.start >= MIN_REGION_SECONDS,
  );
}

describe('the bounds invariant — 0 <= start < end <= duration', () => {
  it('no edit a caller can attempt puts a region outside the media', () => {
    for (const duration of DURATIONS) {
      for (const subject of ADVERSARIAL_REGIONS) {
        for (const time of TIMES) {
          const start = resizeRegionStart(subject, time, duration);
          if (!unchanged(subject, start)) {
            expectInBounds(start, duration, `resizeRegionStart(${subject.id}, ${time}, ${duration})`);
          }
          const end = resizeRegionEnd(subject, time, duration);
          if (!unchanged(subject, end)) {
            expectInBounds(end, duration, `resizeRegionEnd(${subject.id}, ${time}, ${duration})`);
          }
          const moved = moveRegionBy(subject, time, duration);
          if (!unchanged(subject, moved)) {
            expectInBounds(moved, duration, `moveRegionBy(${subject.id}, ${time}, ${duration})`);
          }
        }
      }
    }
  });

  it('a region that was in bounds is still in bounds after any edit', () => {
    for (const duration of DURATIONS) {
      for (const subject of inBoundsRegionsFor(duration)) {
        for (const time of TIMES) {
          expectInBounds(
            resizeRegionStart(subject, time, duration),
            duration,
            `resizeRegionStart(${subject.id}, ${time}, ${duration})`,
          );
          expectInBounds(
            resizeRegionEnd(subject, time, duration),
            duration,
            `resizeRegionEnd(${subject.id}, ${time}, ${duration})`,
          );
          expectInBounds(
            moveRegionBy(subject, time, duration),
            duration,
            `moveRegionBy(${subject.id}, ${time}, ${duration})`,
          );
        }
      }
    }
  });

  it('normalizeRegionBounds either refuses or returns a span inside the media', () => {
    for (const duration of [...DURATIONS, Number.NaN, Number.POSITIVE_INFINITY]) {
      for (const start of TIMES) {
        for (const end of TIMES) {
          const bounds = normalizeRegionBounds(start, end, duration);
          if (bounds !== null) {
            expectInBounds(
              { id: 'n', ...bounds },
              duration,
              `normalizeRegionBounds(${start}, ${end}, ${duration})`,
            );
          }
        }
      }
    }
  });

  it('the default 10s segment fits inside media shorter than 10s', () => {
    for (const duration of DURATIONS) {
      for (const cursor of TIMES) {
        const proposal = buildDefaultRegion(cursor, duration, 'd');
        if (duration >= MIN_REGION_SECONDS) {
          expectInBounds(proposal, duration, `buildDefaultRegion(${cursor}, ${duration})`);
          // Full length when there is room, the whole media when there is not — never longer.
          expect(proposal.end - proposal.start).toBeCloseTo(Math.min(DEFAULT_REGION_SECONDS, duration), 9);
        } else {
          // No usable length: the empty region, which every commit path refuses.
          expect(proposal).toEqual({ id: 'd', start: 0, end: 0 });
        }
      }
    }
  });

  it('every segment that reaches a payload is inside the media', () => {
    for (const duration of DURATIONS) {
      const payload = buildCombineClipPayload({
        regions: ADVERSARIAL_REGIONS,
        session,
        duration,
        id: 'c1',
        title: 'X',
      });
      if (payload === null) {
        // Nothing survived; nothing is sent.
        expect(duration).toBeLessThan(MIN_REGION_SECONDS);
        continue;
      }
      expect(payload.segments.length).toBeGreaterThan(0);
      for (const segment of payload.segments) {
        expectInBounds(
          { id: 's', start: segment.startTime, end: segment.endTime },
          duration,
          `combine segment (duration ${duration})`,
        );
      }
      // The payload's own span agrees with its segments.
      expect(payload.startTime).toBe(payload.segments[0].startTime);
      expect(payload.endTime).toBe(payload.segments[payload.segments.length - 1].endTime);
      expectInBounds({ id: 'p', start: payload.startTime, end: payload.endTime }, duration, 'combine span');

      for (const subject of ADVERSARIAL_REGIONS) {
        const single = buildRegionClipPayload({
          region: subject,
          session,
          duration,
          id: 'c2',
          title: 'X',
          outputMode: 'separate',
        });
        if (single === null) {
          continue;
        }
        expect(single.segments).toHaveLength(1);
        expectInBounds(
          { id: 's', start: single.segments[0].startTime, end: single.segments[0].endTime },
          duration,
          `separate segment ${subject.id} (duration ${duration})`,
        );
      }
    }
  });

  it('clampTime never leaves the media, whatever it is handed', () => {
    for (const duration of [...DURATIONS, Number.NaN, Number.POSITIVE_INFINITY]) {
      for (const time of TIMES) {
        const clamped = clampTime(time, duration);
        expect(Number.isFinite(clamped)).toBe(true);
        expect(clamped).toBeGreaterThanOrEqual(0);
        expect(clamped).toBeLessThanOrEqual(Number.isFinite(duration) ? Math.max(0, duration) : 0);
      }
    }
  });
});

describe('the bug the invariant was violated by — a duration that was a placeholder', () => {
  it('a stale oversized region is not edited into another oversized one', () => {
    // The region was marked while the player still believed the session was 120s long (the fallback
    // for a recording with no metadata record). The media turns out to be 100s.
    const stale = region('stale', 200, 300);
    // The region lies entirely beyond the media, so there is nowhere honest to put it: the edit is
    // refused (the caller keeps what it had) rather than the region being relocated into the media.
    // The reconciliation the controller runs on the same duration change drops it outright.
    expect(resizeRegionStart(stale, 50, 100)).toEqual(stale);
    expect(resizeRegionEnd(stale, 50, 100)).toEqual(stale);
    expect(moveRegionBy(stale, -5, 100)).toEqual(stale);
    expect(reconcileRegion(stale, 100)).toBeNull();
    // ...and nothing of it reaches a payload.
    expect(regionsToSegments([stale], 100)).toEqual([]);
  });

  it('a region straddling the real end is truncated to it', () => {
    const straddling = region('s', 90, 400);
    expect(resizeRegionStart(straddling, 95, 100)).toEqual(region('s', 95, 100));
    expect(regionsToSegments([straddling], 100)).toEqual([{ startTime: 90, endTime: 100 }]);
  });

  it('a duration that is not a number bounds nothing, so every edit is refused', () => {
    // NaN made every comparison false, which turned each clamp into a pass-through; Infinity was the
    // controller's literal default for a session with no declared length.
    for (const duration of [Number.NaN, Number.POSITIVE_INFINITY, 0, -10]) {
      expect(normalizeRegionBounds(10, 20, duration)).toBeNull();
      expect(resizeRegionStart(region('a', 10, 20), 15, duration)).toEqual(region('a', 10, 20));
      expect(resizeRegionEnd(region('a', 10, 20), 15, duration)).toEqual(region('a', 10, 20));
      expect(moveRegionBy(region('a', 10, 20), 5, duration)).toEqual(region('a', 10, 20));
      expect(regionsToSegments([region('a', 10, 20)], duration)).toEqual([]);
      expect(
        buildCombineClipPayload({ regions: [region('a', 10, 20)], session, duration, id: 'c', title: 'X' }),
      ).toBeNull();
    }
  });
});

describe('reconcileRegion(s) — what happens when the real duration is shorter', () => {
  it('keeps a region that already fits, by identity', () => {
    const fits = region('a', 10, 20);
    expect(reconcileRegion(fits, 100)).toBe(fits);
    const list = [region('a', 10, 20), region('b', 40, 50)];
    expect(reconcileRegions(list, 100)).toBe(list);
  });

  it('truncates a region that straddles the real end', () => {
    expect(reconcileRegion(region('a', 5, 20), 8)).toEqual(region('a', 5, 8));
  });

  it('drops a region beyond the real end instead of squashing it into a sliver', () => {
    expect(reconcileRegion(region('a', 37, 47), 8)).toBeNull();
    // Exactly at the end, and close enough to it that nothing usable survives, are both drops.
    expect(reconcileRegion(region('a', 8, 20), 8)).toBeNull();
    expect(reconcileRegion(region('a', 7.9, 20), 8)).toBeNull();
  });

  it('reconciles a list, keeping order and dropping what cannot fit', () => {
    const list = [region('a', 1, 3), region('b', 37, 47), region('c', 5, 20)];
    expect(reconcileRegions(list, 8)).toEqual([region('a', 1, 3), region('c', 5, 8)]);
  });

  it('drops everything when there is no usable duration', () => {
    expect(reconcileRegions([region('a', 1, 3)], 0)).toEqual([]);
    expect(reconcileRegions([region('a', 1, 3)], Number.NaN)).toEqual([]);
    expect(reconcileRegions([region('a', 1, 3)], Number.POSITIVE_INFINITY)).toEqual([]);
  });

  it('is idempotent', () => {
    const once = reconcileRegions([region('a', 5, 20), region('b', 37, 47)], 8);
    expect(reconcileRegions(once, 8)).toBe(once);
  });
});

describe('resolveClipBounds — which duration is authoritative', () => {
  it('prefers the media over the metadata record, because the file is what gets cut', () => {
    // The two disagree when the record was written by a different code path than the file (a
    // recording cut short, a re-encode, an imported record).
    expect(resolveClipBounds(8, 100)).toEqual({ seconds: 8, known: true });
    expect(resolveClipBounds(140, 100)).toEqual({ seconds: 140, known: true });
  });

  it('falls back to the metadata record until the media reports its length', () => {
    expect(resolveClipBounds(undefined, 100)).toEqual({ seconds: 100, known: false });
    // NaN is what a <video> reports before its metadata loads; Infinity is an open-ended stream.
    expect(resolveClipBounds(Number.NaN, 100)).toEqual({ seconds: 100, known: false });
    expect(resolveClipBounds(Number.POSITIVE_INFINITY, 100)).toEqual({ seconds: 100, known: false });
  });

  it('answers "nothing known" rather than a plausible-looking placeholder', () => {
    expect(resolveClipBounds(undefined, undefined)).toEqual({ seconds: 0, known: false });
    expect(resolveClipBounds(undefined, 0)).toEqual({ seconds: 0, known: false });
    expect(resolveClipBounds(0, undefined)).toEqual({ seconds: 0, known: false });
  });
});

describe('markableDuration — only a measured length bounds a segment', () => {
  // MEASURED: a content record declaring 100s in front of a file that is really 9.13s long. The
  // declared length reaches `resolveClipBounds` flagged `known: false`; nothing read the flag, so it
  // was clamped against as if the media had vouched for it. These cases pin the flag being read.
  const declaredSeconds = 100;
  const realSeconds = 9.13;

  it('is the media length once the media has reported one', () => {
    expect(markableDuration(resolveClipBounds(realSeconds, declaredSeconds))).toBe(realSeconds);
    // Longer than declared is still measured, and still the bound: the file is what gets cut.
    expect(markableDuration(resolveClipBounds(140, declaredSeconds))).toBe(140);
  });

  it('is 0 while only the declared length is known, however plausible it looks', () => {
    expect(markableDuration(resolveClipBounds(undefined, declaredSeconds))).toBe(0);
    // NaN is what a <video> reports before its metadata loads; Infinity is an open-ended stream.
    expect(markableDuration(resolveClipBounds(Number.NaN, declaredSeconds))).toBe(0);
    expect(markableDuration(resolveClipBounds(Number.POSITIVE_INFINITY, declaredSeconds))).toBe(0);
    expect(markableDuration(resolveClipBounds(undefined, undefined))).toBe(0);
  });

  it('leaves no marking gesture able to reach past the real media length', () => {
    // The three gestures, all run against the bound in force while only the declaration is known.
    const bound = markableDuration(resolveClipBounds(undefined, declaredSeconds));

    // Mark 10s at 0:95 — the one-click gesture the bug was reported through. Against the declared
    // length this proposed [90, 100], i.e. an end 91s past the last frame that exists.
    const proposal = buildDefaultRegion(95, bound, 'r1');
    expect(proposal.end - proposal.start).toBeLessThan(MIN_REGION_SECONDS);
    expect(proposal.end).toBeLessThanOrEqual(realSeconds);
    // Which is refused rather than committed: nothing survives the gate every mark goes through.
    expect(normalizeRegionBounds(proposal.start, proposal.end, bound)).toBeNull();

    // Mark in / mark out at the same two playhead positions: same answer, same reason.
    expect(normalizeRegionBounds(90, 100, bound)).toBeNull();
    expect(clampTime(95, bound)).toBe(0);

    // And a region that somehow existed against the declaration cannot be edited or sent.
    expect(reconcileRegion(region('r1', 90, 100), bound)).toBeNull();
    expect(regionsToSegments([region('r1', 90, 100)], bound)).toEqual([]);
  });

  it('bounds the same gestures by the media length once it is measured', () => {
    const bound = markableDuration(resolveClipBounds(realSeconds, declaredSeconds));
    // The fixed 10s proposal shrinks to the whole media rather than running past its end.
    expect(buildDefaultRegion(95, bound, 'r1')).toEqual({ id: 'r1', start: 0, end: realSeconds });
    expect(normalizeRegionBounds(90, 100, bound)).toBeNull();
    expect(normalizeRegionBounds(5, 100, bound)).toEqual({ start: 5, end: realSeconds });
  });
});
