// SPDX-License-Identifier: GPL-2.0-or-later
//
// The zoomed timeline — the precision level of the dual timeline. Shows a window of the session
// with bookmark/event icons in detail, region marks (the T9 seam) and a time ruler. Drag pans the
// window, the wheel zooms in/out around the cursor, and clicking jumps the playhead. Bookmarks
// show an info bubble on hover; clicking a bookmark (or a region) jumps to it.

import { useRef, useState } from 'react';
import type { BookmarkItem } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';
import {
  formatTime,
  niceTickInterval,
  panWindow,
  positionToTime,
  timeToPosition,
  type WindowState,
} from './timelineModel';
import { bookmarkColor } from './bookmarks';

export interface ZoomedTimelineProps {
  currentTime: number;
  duration: number;
  window: WindowState;
  bookmarks: BookmarkItem[];
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  onWindowChange(window: WindowState): void;
  onSeek(time: number): void;
  onRegionSelect(region: TimelineRegion): void;
}

interface Bubble {
  left: string;
  text: string;
}

export function ZoomedTimeline({
  currentTime,
  duration,
  window,
  bookmarks,
  regions,
  selectedRegionId,
  onWindowChange,
  onSeek,
  onRegionSelect,
}: ZoomedTimelineProps) {
  const trackRef = useRef<HTMLDivElement>(null);
  const [bubble, setBubble] = useState<Bubble | null>(null);
  const dragRef = useRef<{ pointerId: number; lastClientX: number; dragging: boolean } | null>(null);

  const inWindow = (bookmark: BookmarkItem): boolean =>
    bookmark.time >= window.start && bookmark.time <= window.start + window.seconds;

  function zoomAtClientX(clientX: number, factor: number): void {
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    const focus = positionToTime(clientX, rect, window.start, window.seconds);
    const seconds = Math.min(duration, Math.max(1, window.seconds * factor));
    const start = Math.max(0, focus - ((clientX - rect.left) / rect.width) * seconds);
    onWindowChange({ start, seconds });
  }

  function onPointerDown(event: React.PointerEvent): void {
    const target = event.target as HTMLElement;
    // A bookmark or region click is handled by its own element and jumps the playhead.
    if (target.closest('[data-jump]')) {
      return;
    }
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    if (event.shiftKey) {
      // Shift-click pans the window so the clicked point lands at the left edge.
      const targetTime = positionToTime(event.clientX, rect, window.start, window.seconds);
      onWindowChange({ start: Math.max(0, targetTime), seconds: window.seconds });
      return;
    }
    dragRef.current = { pointerId: event.pointerId, lastClientX: event.clientX, dragging: false };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  }

  function onPointerMove(event: React.PointerEvent): void {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    if (!drag.dragging) {
      // A click still jumps the playhead on pointerup; a drag pans the window instead.
      drag.dragging = Math.abs(event.clientX - drag.lastClientX) > 3;
    }
    if (drag.dragging) {
      const deltaSeconds = (event.clientX - drag.lastClientX) * (window.seconds / rect.width);
      onWindowChange(panWindow(window, -deltaSeconds, duration));
    }
    drag.lastClientX = event.clientX;
  }

  function onPointerUp(event: React.PointerEvent): void {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }
    dragRef.current = null;
    if (!drag.dragging) {
      const rect = trackRef.current?.getBoundingClientRect();
      if (rect) {
        onSeek(positionToTime(event.clientX, rect, window.start, window.seconds));
      }
    }
  }

  const rect = trackRef.current?.getBoundingClientRect() ?? null;
  // Before layout exists the playhead is drawn at the window start.
  const playheadLeft = rect
    ? `${timeToPosition(currentTime, rect, window.start, window.seconds) - rect.left}px`
    : '0px';

  return (
    <div
      ref={trackRef}
      className="timeline-zoomed"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={() => {
        dragRef.current = null;
      }}
      onWheel={(event) => {
        if (event.deltaY === 0) {
          return;
        }
        event.preventDefault();
        // Wheel up zooms in, wheel down zooms out; the cursor stays anchored.
        zoomAtClientX(event.clientX, event.deltaY > 0 ? 1.25 : 0.8);
      }}
    >
      <div className="timeline-ruler">
        {rulerTicks(window).map((tick) => (
          <span key={tick} className="timeline-ruler-tick" style={{ left: tickLeft(tick, window, rect) }}>
            {formatTime(tick)}
          </span>
        ))}
      </div>

      <div className="timeline-track">
        {regions.map((region) => (
          <button
            key={region.id}
            type="button"
            data-jump
            className={`timeline-region ${selectedRegionId === region.id ? 'selected' : ''}`}
            style={{
              left: regionLeft(region.start, window, rect),
              width: regionWidth(region, window, rect),
            }}
            aria-label={`Region ${formatTime(region.start)}–${formatTime(region.end)}`}
            onClick={() => onRegionSelect(region)}
          />
        ))}

        {bookmarks.filter(inWindow).map((bookmark) => {
          const left = bookmarkLeft(bookmark.time, window, rect);
          return (
            <button
              key={bookmark.id}
              type="button"
              data-jump
              className="timeline-bookmark"
              style={{ left, background: bookmarkColor(bookmark.type) }}
              aria-label={`${bookmark.type} at ${formatTime(bookmark.time)}`}
              onClick={() => onSeek(bookmark.time)}
              onPointerEnter={() =>
                setBubble({ left, text: bookmarkBubbleText(bookmark) })
              }
              onPointerLeave={() => setBubble(null)}
            />
          );
        })}

        {bubble && (
          <div className="timeline-bubble" style={{ left: bubble.left }}>
            {bubble.text}
          </div>
        )}

        <div className="timeline-playhead" style={{ left: playheadLeft }} />
      </div>

      <div className="timeline-scale">
        <span>{formatTime(window.start)}</span>
        <span>{formatTime(Math.min(window.start + window.seconds, duration))}</span>
      </div>
    </div>
  );
}

function rulerTicks(window: WindowState): number[] {
  const interval = niceTickInterval(window.seconds);
  const first = Math.ceil(window.start / interval) * interval;
  const ticks: number[] = [];
  for (let t = first; t <= window.start + window.seconds; t += interval) {
    ticks.push(t);
  }
  return ticks;
}

function tickLeft(tick: number, window: WindowState, rect: { left: number; width: number } | null): string {
  if (!rect) {
    return '0';
  }
  return `${timeToPosition(tick, rect, window.start, window.seconds) - rect.left}px`;
}

function bookmarkLeft(time: number, window: WindowState, rect: { left: number; width: number } | null): string {
  if (!rect) {
    return '0';
  }
  return `${timeToPosition(time, rect, window.start, window.seconds) - rect.left}px`;
}

function regionLeft(start: number, window: WindowState, rect: { left: number; width: number } | null): string {
  if (!rect) {
    return '0';
  }
  return `${timeToPosition(start, rect, window.start, window.seconds) - rect.left}px`;
}

function regionWidth(region: TimelineRegion, window: WindowState, rect: { left: number; width: number } | null): string {
  if (!rect) {
    return '0px';
  }
  const left = timeToPosition(region.start, rect, window.start, window.seconds);
  const right = timeToPosition(region.end, rect, window.start, window.seconds);
  return `${Math.max(0, right - left)}px`;
}

function bookmarkBubbleText(bookmark: BookmarkItem): string {
  const type = bookmark.subtype ? `${bookmark.type} · ${bookmark.subtype}` : bookmark.type;
  const label = bookmark.label ? ` — ${bookmark.label}` : '';
  return `${type} · ${formatTime(bookmark.time)}${label}`;
}
