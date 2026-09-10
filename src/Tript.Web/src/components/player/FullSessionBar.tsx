// SPDX-License-Identifier: GPL-2.0-or-later

import { useRef } from 'react';
import type { BookmarkItem } from '../../ipc/protocol';
import { clamp, positionToTime } from './timelineModel';
import type { WindowState } from './timelineModel';
import { bookmarkColor } from './bookmarks';
import { releasePointerFocus } from '../ui/pointerFocus';

export interface FullSessionBarProps {
  currentTime: number;
  duration: number;
  bookmarks: BookmarkItem[];
  window: WindowState | null;
  onSeek(time: number): void;
}

export function FullSessionBar({
  currentTime,
  duration,
  bookmarks,
  window,
  onSeek,
}: FullSessionBarProps) {
  const barRef = useRef<HTMLDivElement>(null);

  function seekFromPointer(event: React.PointerEvent): void {
    const rect = barRef.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    onSeek(positionToTime(event.clientX, rect, 0, duration));
  }

  const playFraction = duration > 0 ? clamp(currentTime / duration, 0, 1) : 0;
  const windowStartFraction = window ? clamp(window.start / duration, 0, 1) : 0;
  const windowEndFraction = window ? clamp((window.start + window.seconds) / duration, 0, 1) : 0;

  return (
    <div
      ref={barRef}
      className="timeline-bar"
      role="slider"
      aria-label="Recording position"
      aria-valuemin={0}
      aria-valuemax={Math.round(duration)}
      aria-valuenow={Math.round(currentTime)}
      tabIndex={0}
      onPointerDown={seekFromPointer}
      onKeyDown={(event) => {
        if (event.key === 'ArrowLeft') {
          onSeek(currentTime - 5);
          event.preventDefault();
        } else if (event.key === 'ArrowRight') {
          onSeek(currentTime + 5);
          event.preventDefault();
        }
      }}
    >
      <div className="timeline-bar-fill" style={{ width: `${(playFraction * 100).toFixed(4)}%` }} />
      {bookmarks.map((bookmark) => {
        const fraction = duration > 0 ? clamp(bookmark.time / duration, 0, 1) : 0;
        return (
          <button
            key={bookmark.id}
            type="button"
            className="timeline-tick"
            style={{ left: `${fraction * 100}%`, background: bookmarkColor(bookmark.type) }}
            aria-label={`Bookmark at ${bookmark.time.toFixed(1)}s`}
            onPointerDown={(event) => {
              event.stopPropagation();
              onSeek(bookmark.time);
            }}
            onPointerUp={releasePointerFocus}
          />
        );
      })}
      {window && (
        <div
          className="timeline-window-bracket"
          style={{ left: `${windowStartFraction * 100}%`, width: `${(windowEndFraction - windowStartFraction) * 100}%` }}
        />
      )}
      <div className="timeline-playhead" style={{ left: `${playFraction * 100}%` }} />
    </div>
  );
}
