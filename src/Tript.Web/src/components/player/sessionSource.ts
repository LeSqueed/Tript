// SPDX-License-Identifier: GPL-2.0-or-later

import type { BookmarkItem, ContentItem } from '../../ipc/protocol';

export interface SessionSource {
  getSessions(): ContentItem[];
  getBookmarks(item: ContentItem): BookmarkItem[];
  observeSessions?(onChange: () => void): () => void;
  getVersion?: () => number;
  getClips?: () => ContentItem[];
  getItems?: () => ContentItem[];
}

export const DEFAULT_SESSION_SECONDS = 120;

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
