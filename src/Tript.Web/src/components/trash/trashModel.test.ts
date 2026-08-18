// SPDX-License-Identifier: GPL-2.0-or-later
//
// The trash model, tested without a DOM. Two things are being pinned down: that a `trash` push is
// read as epoch SECONDS and survives entries the backend under-fills, and that every sentence about
// retention comes from the wire — including the "never auto-purge" case, which a hardcoded 24 would
// turn into a promise the backend never made.

import { describe, expect, it } from 'vitest';
import type { TrashEntry } from '../../ipc/protocol';
import {
  DEFAULT_RETENTION_HOURS,
  deletionNotice,
  formatDeletedAt,
  formatElapsed,
  formatPurgeAt,
  formatRetention,
  formatTrashDuration,
  formatTrashSize,
  parseTrashMessage,
  retentionNotice,
  sortTrashEntries,
  trashEntryLabel,
  trashTypeLabel,
} from './trashModel';

/** A fixed "now": 2026-08-17T00:00:00Z in epoch seconds. */
const NOW = 1787011200;
const HOUR = 3600;
const DAY = 24 * HOUR;

function entry(overrides: Partial<TrashEntry> & { id: string }): TrashEntry {
  return {
    contentType: 'recording',
    fileName: `${overrides.id}.mp4`,
    deletedAt: NOW - HOUR,
    purgeAt: NOW + 23 * HOUR,
    ...overrides,
  };
}

describe('parseTrashMessage', () => {
  it('reads the entries and the retention off the push', () => {
    const state = parseTrashMessage({ entries: [entry({ id: 'one' })], retentionHours: 72 });
    expect(state?.retentionHours).toBe(72);
    expect(state?.entries.map((e) => e.id)).toEqual(['one']);
  });

  it('rejects a frame that is not a trash message rather than half-building one', () => {
    expect(parseTrashMessage(null)).toBeNull();
    expect(parseTrashMessage('trash')).toBeNull();
    expect(parseTrashMessage({ retentionHours: 24 })).toBeNull();
  });

  it('falls back to the backend default when retention is missing or not a number', () => {
    expect(parseTrashMessage({ entries: [] })?.retentionHours).toBe(DEFAULT_RETENTION_HOURS);
    expect(parseTrashMessage({ entries: [], retentionHours: 'lots' })?.retentionHours).toBe(
      DEFAULT_RETENTION_HOURS,
    );
  });

  it('drops entries with no id — there is nothing a restore or purge could name', () => {
    const state = parseTrashMessage({
      entries: [entry({ id: 'good' }), { fileName: 'orphan.mp4', deletedAt: NOW }, 7],
      retentionHours: 24,
    });
    expect(state?.entries.map((e) => e.id)).toEqual(['good']);
  });

  it('returns the entries newest-deleted first', () => {
    const state = parseTrashMessage({
      entries: [
        entry({ id: 'old', deletedAt: NOW - 3 * DAY }),
        entry({ id: 'new', deletedAt: NOW - 60 }),
      ],
      retentionHours: 24,
    });
    expect(state?.entries.map((e) => e.id)).toEqual(['new', 'old']);
  });

  it('never mutates the array it sorts', () => {
    const entries = [entry({ id: 'old', deletedAt: 1 }), entry({ id: 'new', deletedAt: 2 })];
    sortTrashEntries(entries);
    expect(entries.map((e) => e.id)).toEqual(['old', 'new']);
  });
});

describe('reading an entry', () => {
  it('prefers the title and falls back to the file name', () => {
    expect(trashEntryLabel(entry({ id: 'a', title: 'Ranked win' }))).toBe('Ranked win');
    expect(trashEntryLabel(entry({ id: 'a', title: '   ' }))).toBe('a.mp4');
    expect(trashEntryLabel(entry({ id: 'a' }))).toBe('a.mp4');
  });

  it('labels what the entry was', () => {
    expect(trashTypeLabel(entry({ id: 'a' }))).toBe('Session');
    expect(trashTypeLabel(entry({ id: 'a', contentType: 'clip' }))).toBe('Clip');
    expect(trashTypeLabel(entry({ id: 'a', contentType: 'buffer' }))).toBe('Buffer');
  });

  it('omits the size and duration chips rather than showing an empty one', () => {
    expect(formatTrashSize(entry({ id: 'a' }))).toBeNull();
    expect(formatTrashSize(entry({ id: 'a', fileSizeBytes: 33338 }))).toBe('33 KB');
    expect(formatTrashDuration(entry({ id: 'a' }))).toBeNull();
    expect(formatTrashDuration(entry({ id: 'a', durationSeconds: 1830 }))).toBe('30:30');
  });
});

describe('times', () => {
  it('states an elapsed span at the coarsest useful unit', () => {
    expect(formatElapsed(5)).toBe('less than a minute');
    expect(formatElapsed(60)).toBe('1 minute');
    expect(formatElapsed(30 * 60)).toBe('30 minutes');
    expect(formatElapsed(HOUR)).toBe('1 hour');
    expect(formatElapsed(22 * HOUR)).toBe('22 hours');
    expect(formatElapsed(3 * DAY)).toBe('3 days');
  });

  it('says when the entry was deleted, and admits when it cannot', () => {
    expect(formatDeletedAt(entry({ id: 'a', deletedAt: NOW - 2 * HOUR }), NOW)).toBe(
      'Deleted 2 hours ago',
    );
    expect(formatDeletedAt(entry({ id: 'a', deletedAt: 0 }), NOW)).toBe(
      'Deleted at an unknown time',
    );
  });

  it('says when the entry goes for good — including "never", which is a promise, not a blank', () => {
    expect(formatPurgeAt(entry({ id: 'a', purgeAt: NOW + 22 * HOUR }), NOW)).toBe(
      'Deleted for good in 22 hours',
    );
    expect(formatPurgeAt(entry({ id: 'a', purgeAt: 0 }), NOW)).toBe(
      'Kept until you empty the trash',
    );
    expect(formatPurgeAt(entry({ id: 'a', purgeAt: NOW - 60 }), NOW)).toBe('Due to be deleted');
  });
});

describe('retention', () => {
  it('phrases the window in the largest whole unit that fits', () => {
    expect(formatRetention(1)).toBe('1 hour');
    expect(formatRetention(24)).toBe('1 day');
    expect(formatRetention(72)).toBe('3 days');
    expect(formatRetention(36)).toBe('36 hours');
  });

  it('reads a non-positive retention as "never auto-purge"', () => {
    expect(formatRetention(0)).toBeNull();
    expect(formatRetention(-1)).toBeNull();
    expect(retentionNotice(0)).toContain('until you delete them permanently');
    expect(retentionNotice(24)).toBe('Items are kept for 1 day, then deleted for good.');
  });
});

describe('deletionNotice', () => {
  it('quotes the backend retention rather than a constant', () => {
    expect(deletionNotice(1, false, 72)).toBe(
      '1 item will be moved to the trash, where it can be restored for the next 3 days.',
    );
    expect(deletionNotice(3, false, 24)).toBe(
      '3 items will be moved to the trash, where they can be restored for the next 1 day.',
    );
  });

  it('promises no window at all when the backend never auto-purges', () => {
    expect(deletionNotice(2, false, 0)).toBe(
      '2 items will be moved to the trash, where they can be restored until you empty it.',
    );
  });

  it('says plainly that the permanent path cannot be undone', () => {
    expect(deletionNotice(2, true, 24)).toBe(
      '2 items will be deleted from disk immediately. This cannot be undone.',
    );
  });
});
