// SPDX-License-Identifier: GPL-2.0-or-later

import { useMemo } from 'react';
import type { BookmarkItem } from '../../ipc/protocol';
import { Button, Checkbox } from '../ui/controls';
import { formatTime } from './timelineModel';
import { bookmarkColor, bookmarkKindLabel, filterBookmarks, summarizeBookmarks } from './bookmarks';

export interface BookmarkPanelProps {
  bookmarks: BookmarkItem[];
  currentTime: number;
  hiddenKinds: ReadonlySet<string>;
  onToggleKind(type: string): void;
  onSeek(time: number): void;
  onDelete?(bookmark: BookmarkItem): void;
}

export function activeBookmarkId(bookmarks: BookmarkItem[], currentTime: number): string | null {
  let active: BookmarkItem | null = null;
  for (const bookmark of bookmarks) {
    if (bookmark.time <= currentTime + 0.25 && (active === null || bookmark.time >= active.time)) {
      active = bookmark;
    }
  }
  return active?.id ?? null;
}

export function BookmarkPanel({
  bookmarks,
  currentTime,
  hiddenKinds,
  onToggleKind,
  onSeek,
  onDelete,
}: BookmarkPanelProps) {
  const kinds = useMemo(() => summarizeBookmarks(bookmarks), [bookmarks]);
  const visible = useMemo(
    () => filterBookmarks(bookmarks, hiddenKinds).slice().sort((a, b) => a.time - b.time),
    [bookmarks, hiddenKinds],
  );
  const activeId = activeBookmarkId(visible, currentTime);

  return (
    <div className="player-panel-body">
      <div className="bookmark-filters">
        {kinds.map((kind) => (
          <label key={kind.type} className="bookmark-filter">
            <Checkbox
              checked={!hiddenKinds.has(kind.type)}
              onChange={() => onToggleKind(kind.type)}
              aria-label={`Show ${kind.label} bookmarks`}
            />
            <span className="bookmark-swatch" style={{ background: kind.color }} aria-hidden="true" />
            <span className="bookmark-filter-label">{kind.label}</span>
            <span className="pill-muted">{kind.count}</span>
          </label>
        ))}
      </div>

      {visible.length === 0 ? (
        <p className="bookmark-empty muted small">
          {bookmarks.length === 0
            ? 'No bookmarks on this recording yet.'
            : 'Nothing matches the types you picked.'}
        </p>
      ) : (
        <ul className="bookmark-list">
          {visible.map((bookmark) => (
            <li
              key={bookmark.id}
              className={bookmark.id === activeId ? 'bookmark-row active' : 'bookmark-row'}
            >
              <button
                type="button"
                className="bookmark-row-seek"
                onClick={() => onSeek(bookmark.time)}
                aria-current={bookmark.id === activeId ? 'true' : undefined}
                aria-label={`Play from ${bookmarkKindLabel(bookmark.type)} at ${formatTime(bookmark.time)}`}
              >
                <span
                  className="bookmark-swatch"
                  style={{ background: bookmarkColor(bookmark.type) }}
                  aria-hidden="true"
                />
                <span className="bookmark-row-label">{bookmarkKindLabel(bookmark.type)}</span>
                <span className="bookmark-row-time">{formatTime(bookmark.time)}</span>
              </button>
              {onDelete && bookmark.type === 'manual' && (
                <Button
                  variant="ghost"
                  size="icon"
                  icon="trash"
                  onClick={() => onDelete(bookmark)}
                  title="Remove this bookmark"
                  aria-label={`Remove the bookmark at ${formatTime(bookmark.time)}`}
                />
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
