// SPDX-License-Identifier: GPL-2.0-or-later
//
// The in-player clipping UI integration tests. These drive the real PlayerView (video + dual
// timeline + the clip dialog + segment looping) and assert the clip payloads the dialog sends,
// the loop decisions that seek the playhead, and the importProgress result surface.
//
// jsdom has no layout, so the same geometry stubs as PlayerView.test.tsx apply: every element is
// a 100px-wide rect at x=0, so a pointer clientX maps one-to-one to a time in a 100-second session.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { PlayerView } from './PlayerView';
import type { SessionSource } from './player/sessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';

const session = (n: number, filePath: string): ContentItem => ({
  contentType: 'recording',
  fileName: `session-${n}.mp4`,
  filePath,
  title: `Session ${n}`,
  startTime: 0,
  endTime: 100,
});

const source: SessionSource = {
  getSessions: () => [session(1, 'sessions/a.mp4')],
  getBookmarks: () => [],
};

function mockClient(): IpcClient & {
  sent: { method: string; parameters?: unknown }[];
  emit(method: string, content: unknown): void;
} {
  const sent: { method: string; parameters?: unknown }[] = [];
  const handlers = new Map<string, Set<(content: unknown) => void>>();
  return {
    sent,
    state: 'disconnected',
    connect: () => {},
    close: () => {},
    send: (method: string, parameters?: unknown) => {
      sent.push({ method, parameters });
    },
    on: (method: string, handler: (content: unknown) => void) => {
      let set = handlers.get(method);
      if (!set) {
        set = new Set();
        handlers.set(method, set);
      }
      set.add(handler);
      return () => {
        set.delete(handler);
      };
    },
    onStateChange: () => () => {},
    emit(method: string, content: unknown) {
      for (const handler of [...(handlers.get(method) ?? [])]) {
        handler(content);
      }
    },
  };
}

function stubLayout(): void {
  Element.prototype.getBoundingClientRect = () =>
    ({ left: 0, top: 0, right: 100, bottom: 44, width: 100, height: 44 }) as DOMRect;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
}

function currentReadout(): string {
  return screen.getByTestId('transport-current').textContent ?? '';
}

/** Open the clip dialog through the real player's "Create clip" footer button. */
function openClipDialog(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Open clip dialog' }));
  });
}

/** The dialog's submit button, scoped to the dialog so it never matches the footer button. */
function createButton(): HTMLElement {
  const dialog = screen.getByRole('dialog', { name: 'Create clip' });
  return within(dialog).getByRole('button', { name: /^Create (clip|1 clip)/ });
}

/** The media reports its length, as `durationchange` does once metadata loads. */
function setVideoDuration(container: HTMLElement, seconds: number): void {
  const video = container.querySelector('video') as HTMLVideoElement;
  act(() => {
    Object.defineProperty(video, 'duration', { configurable: true, writable: true, value: seconds });
    fireEvent.durationChange(video);
  });
}

/**
 * Render the player and let the media report how long it is — which is what a real <video> element
 * does as soon as it has read the file's header, and what jsdom never does on its own.
 *
 * Every test that marks a segment goes through here, because a segment can only be marked against a
 * *measured* length. The session record's declared `endTime` is not one: it is written by a different
 * code path than the file and has been seen claiming 100s in front of a 9.13s file (see the bounds
 * tests at the bottom of this file). `mediaSeconds` defaults to the 100s these tests' geometry assumes,
 * so the measured and declared lengths agree unless a test deliberately makes them disagree.
 */
function renderPlayer(
  client: IpcClient = mockClient(),
  mediaSeconds = 100,
  sessionSource: SessionSource = source,
): HTMLElement {
  const { container } = render(<PlayerView client={client} source={sessionSource} />);
  setVideoDuration(container, mediaSeconds);
  return container;
}

/** Drive the video element's position, as a timeupdate would. */
function setVideoTime(container: HTMLElement, time: number): void {
  const video = container.querySelector('video') as HTMLVideoElement;
  act(() => {
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: time });
    fireEvent.timeUpdate(video);
  });
}

/** Seek the playhead by clicking the full-session bar at a session time. */
function seekTo(container: HTMLElement, time: number): void {
  const bar = container.querySelector('.timeline-bar') as Element;
  fireEvent.pointerDown(bar, { clientX: time, pointerId: 1 });
  fireEvent.pointerUp(bar, { clientX: time, pointerId: 1 });
}

/** The marked segments as the zoomed timeline labels them ("Region 0:37–0:47"). */
function regionLabels(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('.timeline-region')).map(
    (element) => element.getAttribute('aria-label') ?? '',
  );
}

/**
 * Drag a marked segment on the zoomed timeline.
 *
 * The geometry stub makes every rect 100px wide at x=0, and the zoom window is 60s wide (the default
 * window, clamped to the 100s session) — so one pixel is 0.6s and a clientX maps to
 * `window.start + clientX * 0.6`. `grip` picks the gesture: the body slides the segment, an edge
 * trims it.
 */
function dragRegion(
  container: HTMLElement,
  grip: 'body' | 'start' | 'end',
  fromClientX: number,
  toClientX: number,
): void {
  const region = container.querySelector('.timeline-region') as HTMLElement;
  const target =
    grip === 'body' ? region : (region.querySelector(`.timeline-region-handle.${grip}`) as HTMLElement);
  act(() => {
    fireEvent.pointerDown(target, { clientX: fromClientX, pointerId: 7, button: 0 });
    fireEvent.pointerMove(target, { clientX: toClientX, pointerId: 7 });
    fireEvent.pointerUp(target, { clientX: toClientX, pointerId: 7 });
  });
}

/** Mark a 10s segment around the playhead through the player's own control. */
function markDefaultSegment(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Mark segment around the playhead' }));
  });
}

describe('clip dialog — default region from the playbar', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('opening the clip dialog proposes a default region centred on the playbar cursor', () => {
    const container = renderPlayer();
    // Seek the playhead to 42s via the full-session bar.
    seekTo(container, 42);
    expect(currentReadout()).toBe('0:42');
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(dialog).toBeTruthy();
    const row = within(dialog).getByRole('button', { name: /Deselect region 1/ });
    // Default 10s centred on 42 → [37, 47].
    expect(row.textContent).toContain('0:37 – 0:47');
  });

  it('the default region stays clamped to the session at the playhead edges', () => {
    const container = renderPlayer();
    seekTo(container, 98);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const row = within(dialog).getByRole('button', { name: /Deselect region 1/ });
    // Cursor 98, default 10s, clamped to the session → [90, 100] → "1:30 – 1:40".
    expect(row.textContent).toContain('1:30 – 1:40');
  });
});

describe('clip dialog — region list in the player', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('removing the proposed region updates the region list', () => {
    renderPlayer();
    openClipDialog();
    let dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getAllByRole('button', { name: /(Select|Deselect) region 1/ }).length).toBe(1);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove region 1' }));
    dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).queryByRole('button', { name: /(Select|Deselect) region/ })).toBeNull();
    expect(within(dialog).getByText(/No regions yet/)).toBeTruthy();
  });

  it('the region list shows each region with start/end', () => {
    const container = renderPlayer();
    // Seek the playhead to 42 so the default region is [37, 47].
    const bar = container.querySelector('.timeline-bar') as Element;
    fireEvent.pointerDown(bar, { clientX: 42, pointerId: 1 });
    fireEvent.pointerUp(bar, { clientX: 42, pointerId: 1 });
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getByRole('button', { name: /Deselect region 1/ }).textContent).toContain(
      '0:37 – 0:47',
    );
  });
});

describe('clip dialog — combine vs separate payloads through the player', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('combine sends ONE CreateClip with all regions as segments', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    seekTo(container, 42);
    openClipDialog();
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    const params = creates[0].parameters as Record<string, unknown>;
    expect(params.outputMode).toBe('combine');
    expect(params.segments).toEqual([{ startTime: 37, endTime: 47 }]);
    expect(params.title).toBe('Session 1');
  });

  it('separate sends ONE CreateClip per region', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    seekTo(container, 42);
    openClipDialog();
    fireEvent.click(screen.getByRole('radio', { name: /Separate/ }));
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    const params = creates[0].parameters as Record<string, unknown>;
    expect(params.outputMode).toBe('separate');
    const segments = params.segments as { startTime: number; endTime: number }[];
    expect(segments).toHaveLength(1);
    expect(segments[0]).toEqual({ startTime: 37, endTime: 47 });
  });
});

describe('marking segments from the player (in/out points)', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('says how to mark a segment before anything is marked', () => {
    renderPlayer();
    const hint = screen.getByTestId('player-clip-hint').textContent ?? '';
    expect(hint).toMatch(/press I/);
    expect(hint).toMatch(/then O/);
    // Out has nothing to close yet.
    expect(screen.getByRole('button', { name: 'Mark segment out point' })).toHaveProperty('disabled', true);
  });

  it('the in/out buttons mark a segment between the two playhead positions', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Mark segment in point' }));
    });
    // The half-finished mark is visible on the timeline, and Out is now available.
    expect(screen.getByTestId('timeline-mark-in')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Mark segment out point' })).toHaveProperty('disabled', false);
    seekTo(container, 40);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Mark segment out point' }));
    });
    expect(regionLabels(container)).toEqual(['Region 0:20–0:40']);
    // The in point is spent, and the hint now talks about adjusting.
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    expect(screen.getByTestId('player-clip-hint').textContent).toMatch(/1 segment marked/);
  });

  it('closing a segment on the same frame marks nothing and keeps the in point standing', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Mark segment in point' }));
      fireEvent.click(screen.getByRole('button', { name: 'Mark segment out point' }));
    });
    expect(regionLabels(container)).toEqual([]);
    expect(screen.getByTestId('timeline-mark-in')).toBeTruthy();
  });

  it('the I and O keys mark a segment at the playhead', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    act(() => {
      fireEvent.keyDown(window, { key: 'i' });
    });
    seekTo(container, 40);
    act(() => {
      fireEvent.keyDown(window, { key: 'o' });
    });
    expect(regionLabels(container)).toEqual(['Region 0:20–0:40']);
  });

  it('the M key marks a default-length segment around the playhead', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    act(() => {
      fireEvent.keyDown(window, { key: 'm' });
    });
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
  });

  it('the shortcuts stand down while the user is typing in a text field', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    openClipDialog();
    const title = screen.getByPlaceholderText('Clip title');
    act(() => {
      fireEvent.keyDown(title, { key: 'i' });
      fireEvent.keyDown(title, { key: 'm' });
      fireEvent.keyDown(title, { key: 'o' });
    });
    // No in point was set and nothing was marked: only the dialog's own proposal is on the timeline.
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    // The very same key outside the field does fire — so the assertions above are not vacuous.
    act(() => {
      fireEvent.keyDown(window, { key: 'i' });
    });
    expect(screen.getByTestId('timeline-mark-in')).toBeTruthy();
  });

  it('marks made in the player survive opening the dialog, and add up', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:15–0:25', 'Region 0:55–1:05']);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    // The dialog reviews the marks; it does not restart from its own proposal.
    expect(within(dialog).getAllByRole('button', { name: /(Select|Deselect) region \d/ })).toHaveLength(2);
    expect(within(dialog).getByRole('button', { name: /(Select|Deselect) region 1/ }).textContent).toContain(
      '0:15 – 0:25',
    );
  });

  it('removing a marked segment drops it from the timeline too', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    act(() => {
      fireEvent.click(within(dialog).getByRole('button', { name: 'Remove region 1' }));
    });
    expect(regionLabels(container)).toEqual(['Region 0:55–1:05']);
  });

  it('two marked segments become one clip with two segments in combine mode', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    openClipDialog();
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    const params = creates[0].parameters as Record<string, unknown>;
    expect(params.outputMode).toBe('combine');
    expect(params.segments).toEqual([
      { startTime: 15, endTime: 25 },
      { startTime: 55, endTime: 65 },
    ]);
  });

  it('two marked segments become two CreateClip calls in separate mode', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    openClipDialog();
    fireEvent.click(screen.getByRole('radio', { name: /Separate/ }));
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    fireEvent.click(within(dialog).getByRole('button', { name: /^Create 2 clips/ }));
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(2);
    const starts = creates
      .map((create) => (create.parameters as Record<string, unknown>).startTime as number)
      .sort((a, b) => a - b);
    expect(starts).toEqual([15, 55]);
  });
});

describe('adjusting a marked segment with the mouse', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  /** Mark [37, 47] with the playhead at 42, so the zoom window is 12s..72s (0.6s per pixel). */
  function markedPlayer(): HTMLElement {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    return container;
  }

  it('dragging the body slides the segment and preserves its length', () => {
    const container = markedPlayer();
    // +10px → +6s.
    dragRegion(container, 'body', 50, 60);
    expect(regionLabels(container)).toEqual(['Region 0:43–0:53']);
  });

  it('dragging the body past the session end clamps the whole segment, keeping its length', () => {
    const container = markedPlayer();
    dragRegion(container, 'body', 50, 250);
    expect(regionLabels(container)).toEqual(['Region 1:30–1:40']);
  });

  it('dragging the left edge moves only the start', () => {
    const container = markedPlayer();
    // clientX 50 → 12 + 50 * 0.6 = 42s.
    dragRegion(container, 'start', 42, 50);
    expect(regionLabels(container)).toEqual(['Region 0:42–0:47']);
  });

  it('dragging the right edge moves only the end', () => {
    const container = markedPlayer();
    // clientX 70 → 12 + 70 * 0.6 = 54s.
    dragRegion(container, 'end', 58, 70);
    expect(regionLabels(container)).toEqual(['Region 0:37–0:54']);
  });

  it('a press that does not move selects the segment instead of nudging it', () => {
    const container = markedPlayer();
    const region = container.querySelector('.timeline-region') as HTMLElement;
    act(() => {
      fireEvent.pointerDown(region, { clientX: 50, pointerId: 7, button: 0 });
      fireEvent.pointerMove(region, { clientX: 51, pointerId: 7 });
      fireEvent.pointerUp(region, { clientX: 51, pointerId: 7 });
      fireEvent.click(region);
    });
    // Bounds untouched, and the click toggled the loop selection off (marking selected it).
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    expect(region.className).not.toContain('selected');
  });

  it('a drag does not also toggle the loop selection', () => {
    const container = markedPlayer();
    const region = container.querySelector('.timeline-region') as HTMLElement;
    expect(region.className).toContain('selected');
    dragRegion(container, 'body', 50, 60);
    act(() => {
      fireEvent.click(region);
    });
    expect(region.className).toContain('selected');
  });

  it('a region drag never starts a window pan or a seek', () => {
    const container = markedPlayer();
    const scale = container.querySelectorAll('.timeline-scale span');
    const windowBefore = Array.from(scale).map((span) => span.textContent);
    dragRegion(container, 'body', 50, 60);
    expect(currentReadout()).toBe('0:42');
    expect(Array.from(container.querySelectorAll('.timeline-scale span')).map((s) => s.textContent)).toEqual(
      windowBefore,
    );
  });

  it('a dragged segment reaches CreateClip with the dragged bounds', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    seekTo(container, 42);
    markDefaultSegment();
    dragRegion(container, 'end', 58, 70);
    openClipDialog();
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    expect((creates[0].parameters as Record<string, unknown>).segments).toEqual([
      { startTime: 37, endTime: 54 },
    ]);
  });

  it('a dragged bound and the same bound typed in the dialog agree', () => {
    const container = markedPlayer();
    dragRegion(container, 'end', 58, 70);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const endField = within(dialog).getByLabelText('Region 1 end, seconds') as HTMLInputElement;
    expect(Number(endField.value)).toBe(54);
    // Typing the same number is a no-op, which is the point: one seam, one result.
    fireEvent.change(endField, { target: { value: '54' } });
    fireEvent.blur(endField);
    expect(regionLabels(container)).toEqual(['Region 0:37–0:54']);
  });
});

describe('segment looping in the player', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('while playing inside a marked segment, crossing its end loops back to its start', () => {
    const container = renderPlayer();
    // Seek to 42 so the proposed default region is [37, 47].
    seekTo(container, 42);
    // Play state.
    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 42 });
    act(() => {
      fireEvent.play(video);
    });
    // Open the dialog — the default region [37, 47] is selected.
    openClipDialog();
    // Move the playhead inside the segment.
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
    // Cross the segment end (47) as a small step — the loop seeks back to 37.
    setVideoTime(container, 47);
    expect(currentReadout()).toBe('0:37');
  });

  it('leaving the marked segment resumes normal playback', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 42 });
    act(() => {
      fireEvent.play(video);
    });
    openClipDialog();
    // Inside [37, 47] but NOT crossing the end → no loop.
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
    // A deliberate seek past the end → normal playback, no loop.
    setVideoTime(container, 48);
    expect(currentReadout()).toBe('0:48');
  });

  it('does not loop when paused inside a marked segment', () => {
    const container = renderPlayer();
    openClipDialog();
    setVideoTime(container, 47);
    expect(currentReadout()).toBe('0:47');
  });

  it('clicking a segment on the timeline toggles it as the loop target', () => {
    const container = renderPlayer();
    openClipDialog();
    // The proposed region is selected by default (the row reads "Deselect").
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getByRole('button', { name: /Deselect region 1/ })).toBeTruthy();
    // Clicking the region on the timeline deselects it.
    const regionButton = container.querySelector('.timeline-region') as HTMLElement;
    expect(regionButton).not.toBeNull();
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(within(dialog).queryByRole('button', { name: /Deselect region 1/ })).toBeNull();
  });

  it('clicking a segment moves the playhead to its start; clicking again to deselect does not', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    const regionButton = container.querySelector('.timeline-region') as HTMLElement;
    // Marking already armed the loop. Clicking turns it off — a deselect is not "review this
    // segment", so the playhead stays exactly where the user left it.
    expect(regionButton.className).toContain('selected');
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(regionButton.className).not.toContain('selected');
    expect(currentReadout()).toBe('0:42');
    // Clicking again highlights the segment AND puts the playhead on its first frame, so the loop
    // starts at the top instead of only engaging if playback happened to be inside already.
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(regionButton.className).toContain('selected');
    expect(currentReadout()).toBe('0:37');
  });

  it('selecting a segment seeks while paused but never starts playback', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    const regionButton = container.querySelector('.timeline-region') as HTMLElement;
    act(() => {
      fireEvent.click(regionButton);
    });
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(currentReadout()).toBe('0:37');
    expect(screen.getByRole('button', { name: 'Play or pause' }).textContent).toBe('Play');
  });
});

describe('editing the looping segment moves the playhead with it', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('dragging the end before the playhead closes the loop there and then', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 42 });
    act(() => {
      fireEvent.play(video);
    });
    // Playing at 46, inside [37, 47]. The window follows the playhead, so it is 16s..76s and a
    // clientX maps to 16 + x * 0.6.
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
    // Drag the out point back to 43 — behind the playhead. No end crossing will ever be sampled
    // (the end moved through the playhead), so the loop has to close on the edit itself.
    dragRegion(container, 'end', 52, 45);
    expect(regionLabels(container)).toEqual(['Region 0:37–0:43']);
    expect(currentReadout()).toBe('0:37');
  });

  it('dragging the start past the playhead brings the playhead with it', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    // Playhead at 42 inside [37, 47]; the window is 12s..72s, so a clientX maps to 12 + x * 0.6.
    // The in point moves to 45, past the playhead, which would leave it outside the segment.
    dragRegion(container, 'start', 42, 55);
    expect(regionLabels(container)).toEqual(['Region 0:45–0:47']);
    expect(currentReadout()).toBe('0:45');
  });

  it('dragging the start earlier leaves the playhead alone', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    // Extending the segment backwards to 24 while watching at 42: the playhead is still inside, so
    // yanking it to the new in point would fight the user.
    dragRegion(container, 'start', 42, 20);
    expect(regionLabels(container)).toEqual(['Region 0:24–0:47']);
    expect(currentReadout()).toBe('0:42');
  });

  it('editing a segment that is not the loop target never moves the playhead', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    // The second mark is the loop target; the playhead now sits inside the FIRST segment.
    expect(regionLabels(container)).toEqual(['Region 0:15–0:25', 'Region 0:55–1:05']);
    seekTo(container, 20);
    // The window is 0s..60s, so a clientX maps to x * 0.6: the first segment's in point moves to 24,
    // past the playhead. It is not the looping segment, so the playhead does not follow.
    dragRegion(container, 'start', 25, 40);
    expect(regionLabels(container)).toEqual(['Region 0:24–0:25', 'Region 0:55–1:05']);
    expect(currentReadout()).toBe('0:20');
  });
});

describe('importProgress result surface in the player', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('the dialog renders the backend importProgress result', () => {
    const client = mockClient();
    renderPlayer(client);
    openClipDialog();
    fireEvent.click(createButton());
    expect(screen.getByTestId('clip-progress-importing')).toBeTruthy();
    act(() => {
      client.emit('importProgress', { status: 'done', content: {} });
    });
    expect(screen.getByTestId('clip-progress-done')).toBeTruthy();
  });

  it('an error surfaces the message', () => {
    const client = mockClient();
    renderPlayer(client);
    openClipDialog();
    fireEvent.click(createButton());
    act(() => {
      client.emit('importProgress', { status: 'error', error: 'encoder failed' });
    });
    expect(screen.getByTestId('clip-progress-error').textContent).toContain('encoder failed');
  });

  it('the state message audio tracks surface per-track controls in the dialog', () => {
    const client = mockClient();
    renderPlayer(client);
    act(() => {
      client.emit('state', {
        state: {
          recording: false,
          audioTracks: [{ id: 't1', device: 'Game audio', muted: false, volume: 0.8 }],
        },
      });
    });
    openClipDialog();
    expect(screen.getByText('Game audio')).toBeTruthy();
    expect(screen.getByLabelText('Game audio volume')).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------------------------
// Segment bounds against the *real* media length.
//
// The player starts on a provisional length — the session's declared `endTime`, or a placeholder
// constant when the content record carries none — and only learns the truth when the <video> element
// reports its metadata. No segment may be marked against a provisional length, and any segment that
// outlives the length it was marked against must be corrected, which is what these tests pin.

function markInAtPlayhead(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Mark segment in point' }));
  });
}

function markOutAtPlayhead(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Mark segment out point' }));
  });
}

describe('segments stay inside the real media length', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('reconciles marks the media then contradicts, and sends only what fits', () => {
    const client = mockClient();
    // The media reports 100s and both marks are made against that. A duration is not a promise: the
    // element revises it as it reads further (an estimate for a fragmented or streamed file), and a
    // revision downwards leaves marks pointing past a last frame that turned out not to exist.
    const container = renderPlayer(client);
    seekTo(container, 5);
    markInAtPlayhead();
    seekTo(container, 15);
    markOutAtPlayhead();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:05–0:15', 'Region 0:37–0:47']);

    // The media revises its length down to 8 seconds. The straddling segment is truncated to the real
    // end; the one lying entirely beyond it is dropped rather than squashed into a sliver.
    setVideoDuration(container, 8);
    expect(regionLabels(container)).toEqual(['Region 0:05–0:08']);

    openClipDialog();
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    const params = creates[0].parameters as Record<string, unknown>;
    expect(params.segments).toEqual([{ startTime: 5, endTime: 8 }]);
    expect(params.endTime).toBe(8);
  });

  it('the marked 10s segment fits inside a video shorter than 10s', () => {
    const client = mockClient();
    const container = renderPlayer(client, 3);
    markDefaultSegment();
    // The fixed 10s proposal shrinks to the whole media instead of running 7s past its end.
    expect(regionLabels(container)).toEqual(['Region 0:00–0:03']);
    openClipDialog();
    fireEvent.click(createButton());
    const params = (client.sent.filter((c) => c.method === 'CreateClip')[0].parameters ?? {}) as Record<
      string,
      unknown
    >;
    expect(params.segments).toEqual([{ startTime: 0, endTime: 3 }]);
  });

  it('the dialog proposes a default region inside the real media, not the declared length', () => {
    renderPlayer(mockClient(), 3);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getByRole('button', { name: /Deselect region 1/ }).textContent).toContain(
      '0:00 – 0:03',
    );
  });

  it('an out-of-bounds segment cannot be typed in the dialog either', () => {
    const container = renderPlayer(mockClient(), 8);
    markDefaultSegment();
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const endField = within(dialog).getByLabelText('Region 1 end, seconds') as HTMLInputElement;
    fireEvent.change(endField, { target: { value: '95' } });
    fireEvent.blur(endField);
    expect(regionLabels(container)).toEqual(['Region 0:00–0:08']);
  });

  it('a dragged segment cannot be pulled past the real end', () => {
    const container = renderPlayer(mockClient(), 8);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:00–0:08']);
    // Drag the right edge far off the end of the timeline: it parks on the last frame.
    dragRegion(container, 'end', 90, 400);
    const labels = regionLabels(container);
    expect(labels).toHaveLength(1);
    expect(labels[0]).toBe('Region 0:00–0:08');
  });

  it('refuses all three marking gestures while only the DECLARED length is known', () => {
    // MEASURED: the content record declares 100s (see `session`) for a file that is really 9.13s long.
    // Records are written by a different code path than the file — a recording a crash cut short, a
    // re-encode, an imported record — so the declared length can overstate the media by any amount,
    // and this one overstates it by 91 seconds.
    //
    // The player used to clamp marks against it as soon as it had it: `resolveClipBounds` returned the
    // declared length flagged `known: false`, nothing read the flag, and `Mark 10s` at 1:35 produced a
    // segment ending at 1:40 — 91s past the last frame that exists. The regions were corrected later,
    // when the media finally reported its own length, but the mark was visibly wrong the moment it was
    // made, and Create before that point sent the wrong bounds to the backend.
    const realSeconds = 9.13;
    const client = mockClient();
    // No `renderPlayer` here on purpose: nothing has been measured yet, which is the whole case.
    const { container } = render(<PlayerView client={client} source={source} />);
    seekTo(container, 95);
    expect(currentReadout()).toBe('1:35');

    // All three gestures are refused — the buttons are disabled, and the keys (which bypass the
    // buttons) are refused by the model itself.
    expect(screen.getByRole('button', { name: 'Mark segment in point' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Mark segment out point' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Mark segment around the playhead' })).toHaveProperty(
      'disabled',
      true,
    );
    act(() => {
      fireEvent.keyDown(window, { key: 'm' });
      fireEvent.keyDown(window, { key: 'i' });
    });
    seekTo(container, 99);
    act(() => {
      fireEvent.keyDown(window, { key: 'o' });
    });
    expect(regionLabels(container)).toEqual([]);
    expect(screen.getByTestId('player-clip-hint').textContent).toMatch(/Waiting for the video length/);

    // Nor can the declared length be smuggled to the backend by creating before the media loads.
    openClipDialog();
    expect(screen.getByText(/No regions yet/)).toBeTruthy();
    expect(client.sent.filter((c) => c.method === 'CreateClip')).toHaveLength(0);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Close clip dialog' }));
    });

    // The media reports the real length. Now the same gestures work, against 9.13s.
    setVideoDuration(container, realSeconds);
    seekTo(container, 95);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:00–0:09']);
    openClipDialog();
    fireEvent.click(createButton());
    const creates = client.sent.filter((c) => c.method === 'CreateClip');
    expect(creates).toHaveLength(1);
    const segments = (creates[0].parameters as Record<string, unknown>).segments as {
      startTime: number;
      endTime: number;
    }[];
    expect(segments).toEqual([{ startTime: 0, endTime: realSeconds }]);
    for (const segment of segments) {
      expect(segment.endTime).toBeLessThanOrEqual(realSeconds);
    }
  });

  it('with no length known at all, marking is refused and the player says why', () => {
    // A recording with no metadata record: the player's own fallback length is a placeholder
    // constant, and marking against it is exactly the bug. Nothing can be marked until the media
    // reports how long it is.
    const noLength: SessionSource = {
      getSessions: () => [{ ...session(1, 'sessions/a.mp4'), endTime: undefined }],
      getBookmarks: () => [],
    };
    const client = mockClient();
    const { container } = render(<PlayerView client={client} source={noLength} />);
    expect(screen.getByRole('button', { name: 'Mark segment in point' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Mark segment around the playhead' })).toHaveProperty(
      'disabled',
      true,
    );
    expect(screen.getByTestId('player-clip-hint').textContent).toMatch(/Waiting for the video length/);
    // The keyboard shortcut is refused too, not just the buttons.
    act(() => {
      fireEvent.keyDown(window, { key: 'm' });
    });
    expect(regionLabels(container)).toEqual([]);
    // Opening the dialog proposes nothing rather than a fabricated span.
    openClipDialog();
    expect(screen.getByText(/No regions yet/)).toBeTruthy();
    expect(client.sent.filter((c) => c.method === 'CreateClip')).toHaveLength(0);

    // Once the media reports its length, marking works against it.
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Close clip dialog' }));
    });
    setVideoDuration(container, 12);
    expect(screen.getByRole('button', { name: 'Mark segment around the playhead' })).toHaveProperty(
      'disabled',
      false,
    );
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:00–0:10']);
  });
});
