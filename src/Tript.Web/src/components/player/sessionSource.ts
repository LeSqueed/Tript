// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player's data seam. The player reads from a `SessionSource`; the real implementation is
// IPC-backed (player/ipcSessionSource.ts — fed by the control socket's `content` push). The stub
// below is the alpha placeholder, kept for tests and as a reference for the seam's shape.

import type { BookmarkItem, ContentItem } from '../../ipc/protocol';

export interface SessionSource {
  /** All sessions in the current context, ordered. The player navigates this list. */
  getSessions(): ContentItem[];
  /** Bookmarks for a session. `time` is a seconds offset into the session. */
  getBookmarks(item: ContentItem): BookmarkItem[];
  /**
   * Optional push seam. The IPC-backed source is driven by the `content` push and calls `onChange`
   * to make consumers re-read.
   */
  observeSessions?(onChange: () => void): () => void;
  /**
   * Optional monotonic version, incremented whenever the source's content changes externally. The
   * IPC-backed source implements it; consumers subscribe via `observeSessions` and use `getVersion`
   * as the `useSyncExternalStore` snapshot. A static source has no external updates and omits it.
   */
  getVersion?: () => number;
  /** Manual clips and automated highlights in list order. The IPC-backed source implements it. */
  getClips?: () => ContentItem[];
  /**
   * The WHOLE content list in the backend's own order, sessions and clips interleaved. The library
   * grid needs this: it renders one list over both types, and `getSessions()` concatenated with
   * `getClips()` is not the same list — the concatenation loses the backend's ordering across the
   * two types, which is the order the default "newest first" sort starts from.
   */
  getItems?: () => ContentItem[];
}

/** Fallback session length before video metadata arrives (placeholder data has no media files). */
export const DEFAULT_SESSION_SECONDS = 120;

/** PLACEHOLDER — the alpha session source. Data is fabricated; only the shape is meaningful. */
export const stubSessionSource: SessionSource = {
  getSessions(): ContentItem[] {
    return [
      {
        contentType: 'recording',
        fileName: 'session-1.mp4',
        filePath: 'sessions/2026-08-01/session-1.mp4',
        title: 'Recording 1',
        startTime: 0,
        endTime: 120,
      },
      {
        contentType: 'recording',
        fileName: 'session-2.mp4',
        filePath: 'sessions/2026-08-01/session-2.mp4',
        title: 'Recording 2',
        startTime: 0,
        endTime: 90,
      },
      {
        contentType: 'recording',
        fileName: 'session-3.mp4',
        filePath: 'sessions/2026-08-01/session-3.mp4',
        title: 'Recording 3',
        startTime: 0,
        endTime: 300,
      },
    ];
  },

  getBookmarks(item: ContentItem): BookmarkItem[] {
    const sets: Record<string, BookmarkItem[]> = {
      'sessions/2026-08-01/session-1.mp4': [
        { id: 's1-b1', type: 'kill', time: 12.5, label: 'First blood' },
        { id: 's1-b2', type: 'death', time: 34, label: 'Overextended' },
        { id: 's1-b3', type: 'round', time: 78.2, subtype: 'round_start' },
      ],
      'sessions/2026-08-01/session-2.mp4': [
        { id: 's2-b1', type: 'goal', time: 10, label: 'Match point' },
        { id: 's2-b2', type: 'assist', time: 45 },
      ],
      'sessions/2026-08-01/session-3.mp4': [
        { id: 's3-b1', type: 'kill', time: 30, label: 'Clutch' },
        { id: 's3-b2', type: 'death', time: 120 },
        { id: 's3-b3', type: 'event', time: 250, subtype: 'powerup' },
      ],
    };
    return sets[item.filePath] ?? [];
  },
};
