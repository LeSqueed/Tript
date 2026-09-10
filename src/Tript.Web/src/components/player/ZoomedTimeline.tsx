// SPDX-License-Identifier: GPL-2.0-or-later

import { useRef, useState } from 'react';
import type { BookmarkItem } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';
import { moveRegionBy, resizeRegionEnd, resizeRegionStart } from './clipModel';
import {
  formatTime,
  niceTickInterval,
  panWindow,
  positionToTime,
  timeToPosition,
  type WindowState,
} from './timelineModel';
import { bookmarkColor } from './bookmarks';
import { releasePointerFocus } from '../ui/pointerFocus';

export interface ZoomedTimelineProps {
  currentTime: number;
  duration: number;
  window: WindowState;
  bookmarks: BookmarkItem[];
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  markInTime?: number | null;
  onWindowChange(window: WindowState): void;
  onSeek(time: number): void;
  onRegionSelect(region: TimelineRegion): void;
  onRegionChange?(id: string, bounds: { start: number; end: number }): void;
}

interface Bubble {
  left: string;
  text: string;
}

type RegionDragMode = 'move' | 'start' | 'end';

interface RegionDrag {
  pointerId: number;
  mode: RegionDragMode;
  origin: TimelineRegion;
  originClientX: number;
  moved: boolean;
}

const REGION_DRAG_SLOP_PX = 3;

export function ZoomedTimeline({
  currentTime,
  duration,
  window,
  bookmarks,
  regions,
  selectedRegionId,
  markInTime,
  onWindowChange,
  onSeek,
  onRegionSelect,
  onRegionChange,
}: ZoomedTimelineProps) {
  const trackRef = useRef<HTMLDivElement>(null);
  const [bubble, setBubble] = useState<Bubble | null>(null);
  const dragRef = useRef<{ pointerId: number; lastClientX: number; dragging: boolean } | null>(null);
  const regionDragRef = useRef<RegionDrag | null>(null);
  const regionDragMovedRef = useRef(false);
  const [draggingRegionId, setDraggingRegionId] = useState<string | null>(null);

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
    if (target.closest('[data-jump]')) {
      return;
    }
    const rect = trackRef.current?.getBoundingClientRect();
    if (!rect) {
      return;
    }
    if (event.shiftKey) {
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

  function regionBoundsForPointer(
    drag: RegionDrag,
    clientX: number,
    rect: { left: number; width: number },
  ): TimelineRegion {
    if (drag.mode === 'move') {
      const deltaSeconds = (clientX - drag.originClientX) * (window.seconds / rect.width);
      return moveRegionBy(drag.origin, deltaSeconds, duration);
    }
    const time = positionToTime(clientX, rect, window.start, window.seconds);
    return drag.mode === 'start'
      ? resizeRegionStart(drag.origin, time, duration)
      : resizeRegionEnd(drag.origin, time, duration);
  }

  function onRegionPointerDown(event: React.PointerEvent, region: TimelineRegion): void {
    if (!onRegionChange || event.button !== 0) {
      return;
    }
    const target = event.target as HTMLElement;
    const edge = target.closest('[data-region-edge]')?.getAttribute('data-region-edge');
    regionDragRef.current = {
      pointerId: event.pointerId,
      mode: edge === 'start' ? 'start' : edge === 'end' ? 'end' : 'move',
      origin: region,
      originClientX: event.clientX,
      moved: false,
    };
    regionDragMovedRef.current = false;
    (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId);
    event.stopPropagation();
  }

  function onRegionPointerMove(event: React.PointerEvent): void {
    const drag = regionDragRef.current;
    if (!drag || !onRegionChange || drag.pointerId !== event.pointerId) {
      return;
    }
    const trackRect = trackRef.current?.getBoundingClientRect();
    if (!trackRect || trackRect.width <= 0) {
      return;
    }
    if (!drag.moved) {
      if (Math.abs(event.clientX - drag.originClientX) <= REGION_DRAG_SLOP_PX) {
        return;
      }
      drag.moved = true;
      regionDragMovedRef.current = true;
      setDraggingRegionId(drag.origin.id);
    }
    const next = regionBoundsForPointer(drag, event.clientX, trackRect);
    onRegionChange(drag.origin.id, { start: next.start, end: next.end });
  }

  function endRegionDrag(event: React.PointerEvent): void {
    const drag = regionDragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }
    regionDragRef.current = null;
    setDraggingRegionId(null);
    try {
      (event.currentTarget as HTMLElement).releasePointerCapture?.(event.pointerId);
    } catch {
    }
  }

  const rect = trackRef.current?.getBoundingClientRect() ?? null;
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
            className={[
              'timeline-region',
              selectedRegionId === region.id ? 'selected' : '',
              onRegionChange ? 'adjustable' : '',
              draggingRegionId === region.id ? 'dragging' : '',
            ]
              .filter(Boolean)
              .join(' ')}
            style={{
              left: regionLeft(region.start, window, rect),
              width: regionWidth(region, window, rect),
            }}
            aria-label={`Region ${formatTime(region.start)}–${formatTime(region.end)}`}
            title={
              onRegionChange
                ? 'Drag to move the clip · drag an edge to trim it · click to loop it'
                : undefined
            }
            onPointerDown={(event) => onRegionPointerDown(event, region)}
            onPointerMove={onRegionPointerMove}
            onPointerUp={(event) => {
              endRegionDrag(event);
              releasePointerFocus(event);
            }}
            onPointerCancel={(event) => {
              endRegionDrag(event);
              regionDragMovedRef.current = false;
            }}
            onClick={() => {
              if (regionDragMovedRef.current) {
                regionDragMovedRef.current = false;
                return;
              }
              onRegionSelect(region);
            }}
          >
            {onRegionChange && (
              <>
                <span className="timeline-region-handle start" data-region-edge="start" aria-hidden="true" />
                <span className="timeline-region-handle end" data-region-edge="end" aria-hidden="true" />
              </>
            )}
          </button>
        ))}

        {markInTime !== null &&
          markInTime !== undefined &&
          markInTime >= window.start &&
          markInTime <= window.start + window.seconds && (
            <div
              className="timeline-markin"
              data-testid="timeline-mark-in"
              style={{ left: bookmarkLeft(markInTime, window, rect) }}
              title={`In point ${formatTime(markInTime)} — press O to close the segment`}
            />
          )}

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
              onPointerUp={releasePointerFocus}
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

      {}
      {window.seconds < duration - 0.01 && (
        <div className="timeline-scale">
          <span>{formatTime(window.start)}</span>
          <span>{formatTime(Math.min(window.start + window.seconds, duration))}</span>
        </div>
      )}

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
