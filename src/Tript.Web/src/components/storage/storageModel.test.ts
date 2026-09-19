// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { StorageReportMessage } from '../../ipc/protocol';
import {
  GIGABYTE,
  formatStorageSize,
  fromGigabytes,
  gameUsageLabel,
  parseStorageReport,
  parseStorageStatus,
  storageSegments,
  toGigabytes,
} from './storageModel';

function report(patch: Partial<StorageReportMessage> = {}): StorageReportMessage {
  return {
    root: 'T:\\Tript',
    volumeRoot: 'T:\\',
    volumeTotalBytes: 100 * GIGABYTE,
    volumeFreeBytes: 40 * GIGABYTE,
    libraryBytes: 50 * GIGABYTE,
    sessionBytes: 40 * GIGABYTE,
    highlightBytes: 6 * GIGABYTE,
    clipBytes: 2 * GIGABYTE,
    trashBytes: 1 * GIGABYTE,
    sidecarBytes: 1 * GIGABYTE,
    favoriteBytes: 3 * GIGABYTE,
    sessionCount: 4,
    highlightCount: 9,
    clipCount: 2,
    trashCount: 1,
    games: [],
    ...patch,
  };
}

describe('formatStorageSize', () => {
  it('reads zero as a size rather than as nothing', () => {
    expect(formatStorageSize(0)).toBe('0 B');
    expect(formatStorageSize(undefined)).toBe('0 B');
  });

  it('steps up through the units', () => {
    expect(formatStorageSize(2 * GIGABYTE)).toBe('2 GB');
  });
});

describe('gigabyte conversion', () => {
  it('round trips a whole number', () => {
    expect(toGigabytes(fromGigabytes(20))).toBe(20);
  });

  it('keeps one decimal place', () => {
    expect(toGigabytes(fromGigabytes(1.5))).toBe(1.5);
  });
});

describe('parseStorageStatus', () => {
  it('refuses a payload with no pressure it knows', () => {
    expect(parseStorageStatus({ pressure: 'from-the-future' })).toBeNull();
    expect(parseStorageStatus(null)).toBeNull();
    expect(parseStorageStatus('critical')).toBeNull();
  });

  it('fills in what the host left out', () => {
    expect(parseStorageStatus({ pressure: 'ok' })).toEqual({
      pressure: 'ok',
      freeBytes: 0,
      totalBytes: 0,
      minimumFreeBytes: 0,
      warnFreeBytes: 0,
      recordingBlocked: false,
      policyConfirmed: false,
      whenFull: 'PauseRecording',
      keepSharingWhenFull: false,
      volumeRoot: undefined,
      root: '',
      scratchRoot: undefined,
      scratchFreeBytes: 0,
      scratchLow: false,
    });
  });

  it('reads the scratch drive when the host reports one', () => {
    const parsed = parseStorageStatus({
      pressure: 'ok',
      scratchRoot: 'C:',
      scratchFreeBytes: 2 * GIGABYTE,
      scratchLow: true,
    });

    expect(parsed?.scratchRoot).toBe('C:');
    expect(parsed?.scratchFreeBytes).toBe(2 * GIGABYTE);
    expect(parsed?.scratchLow).toBe(true);
  });

  it('falls back to pausing when the policy is not one it knows', () => {
    expect(parseStorageStatus({ pressure: 'ok', whenFull: 'DeleteEverything' })?.whenFull)
      .toBe('PauseRecording');
  });
});

describe('parseStorageReport', () => {
  it('refuses a payload with no root', () => {
    expect(parseStorageReport({ volumeFreeBytes: 10 })).toBeNull();
  });

  it('drops a negative figure rather than drawing it', () => {
    expect(parseStorageReport({ root: 'T:\\', sessionBytes: -5 })?.sessionBytes).toBe(0);
  });

  it('keeps an absent games list as an empty one', () => {
    expect(parseStorageReport({ root: 'T:\\' })?.games).toEqual([]);
  });
});

describe('storageSegments', () => {
  it('splits sessions, highlights and clips apart', () => {
    const segments = storageSegments(report());
    const byKind = Object.fromEntries(segments.map((segment) => [segment.kind, segment.bytes]));

    expect(byKind.sessions).toBe(40 * GIGABYTE);
    expect(byKind.highlights).toBe(6 * GIGABYTE);
    expect(byKind.clips).toBe(2 * GIGABYTE);
    expect(byKind.trash).toBe(1 * GIGABYTE);
  });

  it('counts what other software holds on the same drive', () => {
    const segments = storageSegments(report());
    const elsewhere = segments.find((segment) => segment.kind === 'elsewhere');

    expect(elsewhere?.bytes).toBe(10 * GIGABYTE);
  });

  it('never reports a negative slice when the figures disagree', () => {
    const segments = storageSegments(report({ libraryBytes: 90 * GIGABYTE }));
    const elsewhere = segments.find((segment) => segment.kind === 'elsewhere');

    expect(elsewhere?.bytes).toBe(0);
  });

  it('shares add up to the whole drive', () => {
    const total = storageSegments(report()).reduce((sum, segment) => sum + segment.share, 0);

    expect(total).toBeCloseTo(1, 5);
  });

  it('survives a drive whose size could not be read', () => {
    const segments = storageSegments(report({ volumeTotalBytes: 0, volumeFreeBytes: 0 }));

    expect(segments.every((segment) => Number.isFinite(segment.share))).toBe(true);
  });
});

describe('gameUsageLabel', () => {
  it('names content that belongs to no game', () => {
    expect(gameUsageLabel({ totalBytes: 1, sessionBytes: 1, highlightBytes: 0, clipBytes: 0 }))
      .toBe('Not linked to a game');
  });
});
