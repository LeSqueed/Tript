// SPDX-License-Identifier: GPL-2.0-or-later
//
// The dual-timeline geometry model: the zoom window, time↔position mapping and time formatting.
// Pure functions — the zoom model is deterministic so it can be unit-tested without any DOM.

/** The narrowest zoomed window, seconds. */
export const MIN_WINDOW_SECONDS = 1;

/** The visible zoom window, expressed in seconds into the session. */
export interface WindowState {
  /** Window start, seconds into the session. */
  start: number;
  /** Visible span, seconds. */
  seconds: number;
}

export function clamp(x: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, x));
}

/**
 * The zoomed window: `seconds` long, centred on `focus`, clamped to the session duration.
 * Deterministic — zooming always keeps the playhead inside the window.
 */
export function zoomWindow(focus: number, seconds: number, duration: number): WindowState {
  const d = Math.max(0, duration);
  if (d <= 0) {
    return { start: 0, seconds: 0 };
  }
  const s = clamp(seconds, MIN_WINDOW_SECONDS, d);
  let start = clamp(focus, 0, d) - s / 2;
  if (start < 0) {
    start = 0;
  }
  if (start + s > d) {
    start = Math.max(0, d - s);
  }
  return { start, seconds: s };
}

/** Clamp a window (possibly stale after a duration change) into the session bounds. */
export function clampWindow(window: WindowState, duration: number): WindowState {
  const d = Math.max(0, duration);
  if (d <= 0) {
    return window;
  }
  const seconds = clamp(window.seconds, MIN_WINDOW_SECONDS, d);
  const maxStart = Math.max(0, d - seconds);
  return { start: clamp(window.start, 0, maxStart), seconds };
}

/** Pan the window by a signed delta (seconds). The window size is preserved. */
export function panWindow(window: WindowState, deltaSeconds: number, duration: number): WindowState {
  const d = Math.max(0, duration);
  if (d <= 0) {
    return window;
  }
  const seconds = clamp(window.seconds, MIN_WINDOW_SECONDS, d);
  const maxStart = Math.max(0, d - seconds);
  return { start: clamp(window.start + deltaSeconds, 0, maxStart), seconds };
}

/** Map a pointer clientX within a bounding rect to a session time. */
export function positionToTime(
  clientX: number,
  rect: { left: number; width: number },
  start: number,
  seconds: number,
): number {
  if (rect.width <= 0) {
    return start;
  }
  const fraction = clamp(clientX - rect.left, 0, rect.width) / rect.width;
  return start + fraction * seconds;
}

/** Map a session time to a pixel x-offset within a bounding rect. */
export function timeToPosition(
  time: number,
  rect: { left: number; width: number },
  start: number,
  seconds: number,
): number {
  if (seconds <= 0) {
    return rect.left;
  }
  return rect.left + ((time - start) / seconds) * rect.width;
}

/** Format a seconds count as m:ss or h:mm:ss. */
export function formatTime(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const ss = String(sec).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
}

/** The interval used for ruler labels in a window — roughly six labels across it. */
export function niceTickInterval(windowSeconds: number): number {
  const intervals = [0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
  const target = windowSeconds / 6;
  return intervals.find((i) => i >= target) ?? 3600;
}
