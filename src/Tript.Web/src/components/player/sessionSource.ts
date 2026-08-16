// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player's data seam. The alpha has no backend session model yet, so the player reads from a
// clearly-marked stub source. A later task plugs the real source — fed by the IPC `gameList` /
// `state` / `settings` pushes over the control socket — in behind the same interface; nothing in
// the player changes then.

import type { BookmarkItem, ContentItem } from '../../ipc/protocol';

export interface SessionSource {
  /** All sessions in the current context, ordered. The player navigates this list. */
  getSessions(): ContentItem[];
  /** Bookmarks for a session. `time` is a seconds offset into the session. */
  getBookmarks(item: ContentItem): BookmarkItem[];
  /**
   * Optional push seam. The real source will be driven by IPC messages and calls `onChange` to
   * make the player re-read. Returns an unsubscribe. The stub has no external updates.
   */
  observeSessions?(onChange: () => void): () => void;
}

/** Fallback session length before video metadata arrives (placeholder data has no media files). */
export const DEFAULT_SESSION_SECONDS = 120;

/**
 * PLACEHOLDER — the alpha session source. Data is fabricated; only the shape is meaningful.
 * Replace with an IPC-backed implementation in the task that wires the backend session model.
 */
export const stubSessionSource: SessionSource = {
  getSessions(): ContentItem[] {
    return [
      {
        contentType: 'recording',
        fileName: 'session-1.mp4',
        filePath: 'sessions/2026-08-01/session-1.mp4',
        title: 'Session 1',
        startTime: 0,
        endTime: 120,
      },
      {
        contentType: 'recording',
        fileName: 'session-2.mp4',
        filePath: 'sessions/2026-08-01/session-2.mp4',
        title: 'Session 2',
        startTime: 0,
        endTime: 90,
      },
      {
        contentType: 'recording',
        fileName: 'session-3.mp4',
        filePath: 'sessions/2026-08-01/session-3.mp4',
        title: 'Session 3',
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
