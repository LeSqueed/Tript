// SPDX-License-Identifier: GPL-2.0-or-later
//
// The zoomed timeline — the precision level of the dual timeline. Shows a window of the session
// with bookmark/event icons in detail, region marks (the T9 seam) and a time ruler.

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

export interface ZoomedTimelineProps {
  currentTime: number;
  duration: number;
  window: WindowState;
  bookmarks: BookmarkItem[];
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  /**
   * The pending in point (set at the playhead, waiting for its out point), drawn as a marker so the
   * half-finished mark is visible on the timeline rather than only in the transport row.
   */
  markInTime?: number | null;
  onWindowChange(window: WindowState): void;
  onSeek(time: number): void;
  onRegionSelect(region: TimelineRegion): void;
  /**
   * Commit new bounds for a region. Absent (the read-only region seam) regions stay
   * click-to-select only and no drag handlers are attached at all.
   */
  onRegionChange?(id: string, bounds: { start: number; end: number }): void;
}

interface Bubble {
  left: string;
  text: string;
}

/** Which part of a region the pointer grabbed. */
type RegionDragMode = 'move' | 'start' | 'end';

interface RegionDrag {
  pointerId: number;
  mode: RegionDragMode;
  /** The bounds at pointerdown — every frame is computed from these, so the drag cannot drift. */
  origin: TimelineRegion;
  originClientX: number;
  moved: boolean;
}

/** Slop before a press becomes a drag — the same discipline as the track's pan threshold. */
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
  // A drag that actually moved must not also toggle the loop selection when the click lands.
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

  /** The bounds a pointer position implies for the grabbed region, already clamped. */
  function regionBoundsForPointer(
    drag: RegionDrag,
    clientX: number,
    rect: { left: number; width: number },
  ): TimelineRegion {
    if (drag.mode === 'move') {
      // Pixels → a signed delta in seconds, the same ratio panning uses.
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
    // Capture on the region itself: the pointer keeps addressing this region even when it leaves it.
    (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId);
    // The track's handler already ignores [data-jump] targets; stopping propagation makes the region
    // the sole owner of the gesture regardless of what the track grows into later.
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
        // Still a click, not a drag — a steady hand must not nudge the bounds by a pixel.
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
      // The capture was already released (or never granted) — nothing to undo.
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
                ? 'Drag to move the segment · drag an edge to trim it · click to loop it'
                : undefined
            }
            onPointerDown={(event) => onRegionPointerDown(event, region)}
            onPointerMove={onRegionPointerMove}
            onPointerUp={endRegionDrag}
            onPointerCancel={(event) => {
              endRegionDrag(event);
              regionDragMovedRef.current = false;
            }}
            onClick={() => {
              if (regionDragMovedRef.current) {
                // The pointer moved: the gesture was an adjustment, not a selection.
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
