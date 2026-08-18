// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player + dual synced timeline tests. The sync model is single-source: both timeline levels
// render `currentTime`, and the video is the driver.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PlayerView } from './PlayerView';
import type { SessionSource } from './player/sessionSource';
import type { BookmarkItem, ContentItem } from '../ipc/protocol';
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
  getSessions: () => [
    session(1, 'sessions/a.mp4'),
    session(2, 'sessions/b.mp4'),
    session(3, 'sessions/c.mp4'),
  ],
  getBookmarks: (item: ContentItem): BookmarkItem[] => {
    if (item.filePath === 'sessions/a.mp4') {
      return [
        { id: 'k1', type: 'kill', time: 20, label: 'Clutch' },
        { id: 'k2', type: 'goal', time: 80 },
      ];
    }
    return [];
  },
};

/** A minimal IpcClient double that records sent commands for assertion. */
function mockClient(): IpcClient & { sent: { method: string; parameters?: unknown }[] } {
  const sent: { method: string; parameters?: unknown }[] = [];
  return {
    sent,
    state: 'disconnected',
    connect: () => {},
    close: () => {},
    send: (method: string, parameters?: unknown) => {
      sent.push({ method, parameters });
    },
    on: () => () => {},
    onStateChange: () => () => {},
  };
}

function renderPlayer() {
  return render(<PlayerView client={mockClient()} source={source} />);
}

function currentReadout(): string {
  return screen.getByTestId('transport-current').textContent ?? '';
}

/**
 * Give the DOM the fixed test geometry. jsdom has no layout, so every element reports the same
 * 100px-wide rect at x=0 (a pointer clientX maps one-to-one to a time in a 100-second session),
 * and pointer capture is a no-op. Stubbed on the prototype so the stub is seen by whichever DOM
 * node the component ref holds.
 */
function stubLayout(): void {
  Element.prototype.getBoundingClientRect = () =>
    ({ left: 0, top: 0, right: 100, bottom: 44, width: 100, height: 44 }) as DOMRect;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
}

/** Click the full-session bar at a session time (rect left 0, width 100 → clientX = time). */
function clickBarAt(container: HTMLElement, time: number): void {
  const element = container.querySelector('.timeline-bar');
  expect(element).not.toBeNull();
  fireEvent.pointerDown(element as Element, { clientX: time, pointerId: 1 });
  fireEvent.pointerUp(element as Element, { clientX: time, pointerId: 1 });
}

/** Click inside the zoomed track at a window-relative time. */
function clickZoomedAt(container: HTMLElement, windowTime: number, windowStart = 0, windowSeconds = 100): void {
  const track = container.querySelector('.timeline-track');
  expect(track).not.toBeNull();
  const clientX = ((windowTime - windowStart) / windowSeconds) * 100;
  fireEvent.pointerDown(track as Element, { clientX, pointerId: 1 });
  fireEvent.pointerUp(track as Element, { clientX, pointerId: 1 });
}

function pointerEnter(element: Element, clientX: number): void {
  fireEvent.pointerEnter(element, { clientX });
}

describe('PlayerView', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('renders the session title and the two timeline levels', () => {
    const { container } = renderPlayer();
    expect(screen.getByTestId('player-title').textContent).toBe('Session 1');
    expect(container.querySelector('.timeline-bar')).not.toBeNull();
    expect(container.querySelector('.timeline-zoomed')).not.toBeNull();
    expect(screen.getByTestId('transport-duration').textContent).toBe('1:40');
  });

  it('renders the bookmark ticks on the full-session bar', () => {
    const { container } = renderPlayer();
    const ticks = container.querySelectorAll('.timeline-tick');
    expect(ticks.length).toBe(2);
    // 20s of 100s → left 20%.
    expect((ticks[0] as HTMLElement).style.left).toBe('20%');
  });

  it('keeps the two timelines synced: full-session bar seek updates the transport readout', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 42));
    expect(currentReadout()).toBe('0:42');
  });

  it('shows the video source for the session', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    expect(video.src).toContain('/api/content/sessions/a.mp4');
  });

  it('resets the playhead when navigating to another session', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 80));
    expect(currentReadout()).toBe('1:20');
    fireEvent.click(screen.getByRole('button', { name: 'Next session' }));
    expect(screen.getByTestId('player-title').textContent).toBe('Session 2');
    expect(currentReadout()).toBe('0:00');
  });
});

describe('dual timeline sync', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('moves the playhead in the zoomed timeline and the full-session bar follows', () => {
    const { container } = renderPlayer();
    // Initial window {0, 60}: clientX 60 maps to 60% of 60s = 36s.
    act(() => clickZoomedAt(container, 36, 0, 60));
    expect(currentReadout()).toBe('0:36');
    const fill = container.querySelector('.timeline-bar-fill') as HTMLElement;
    expect(fill.style.width).toBe('36%');
  });

  it('moves the playhead on the full-session bar and the zoomed timeline follows', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 42));
    const track = container.querySelector('.timeline-track') as HTMLElement;
    expect(track).not.toBeNull();
    // The seek recentres the window on 42s: zoomWindow(42, 60, 100) = {12, 60}. The playhead at
    // 42s sits at (42-12)/60 of the 100px track → 50px.
    const playhead = track.querySelector('.timeline-playhead') as HTMLElement;
    expect(playhead).not.toBeNull();
    expect(playhead.style.left).toBe('50px');
  });

  it('a video timeupdate moves both timelines', () => {
    const { container } = renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    act(() => {
      Object.defineProperty(video, 'currentTime', { configurable: true, value: 55 });
      fireEvent.timeUpdate(video);
    });
    expect(currentReadout()).toBe('0:55');
    const fill = container.querySelector('.timeline-bar-fill') as HTMLElement;
    expect(fill.style.width).toBe('55%');
  });
});

describe('bookmark interaction', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('clicking a full-session tick jumps the playhead to the bookmark time', () => {
    const { container } = renderPlayer();
    act(() => {
      fireEvent.pointerDown(container.querySelectorAll('.timeline-tick')[0] as Element, { clientX: 20, pointerId: 1 });
    });
    expect(currentReadout()).toBe('0:20');
  });

  it('clicking a zoomed bookmark jumps the playhead to the bookmark time', () => {
    const { container } = renderPlayer();
    const bookmark = container.querySelector('.timeline-bookmark') as HTMLElement;
    expect(bookmark).not.toBeNull();
    act(() => {
      fireEvent.click(bookmark);
    });
    expect(currentReadout()).toBe('0:20');
  });

  it('zoomed bookmarks show an info bubble on hover', () => {
    const { container } = renderPlayer();
    const bookmark = container.querySelector('.timeline-bookmark') as HTMLElement;
    pointerEnter(bookmark, 20);
    const bubble = container.querySelector('.timeline-bubble');
    expect(bubble).not.toBeNull();
    expect(bubble?.textContent).toContain('kill');
    expect(bubble?.textContent).toContain('0:20');
  });
});

describe('zoom model', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('a wheel zoom-in shrinks the visible window', () => {
    const { container } = renderPlayer();
    // Initial window {0, 60}: click the zoomed track at clientX 36 (→ 36s). Recentred {36, 60}.
    act(() => clickZoomedAt(container, 36, 0, 60));
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    // 60s × 0.8 = 48s window.
    expect(screen.getByText(/Zoom window: 48\.0s/)).toBeTruthy();
  });

  it('a wheel zoom-out grows the visible window back', () => {
    const { container } = renderPlayer();
    act(() => clickZoomedAt(container, 36, 0, 60));
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: 100 });
    // 48s × 1.25 = 60s window.
    expect(screen.getByText(/Zoom window: 60\.0s/)).toBeTruthy();
  });

  it('a full-session bar seek recentres the zoom window on the playhead', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 80));
    const scale = container.querySelector('.timeline-scale');
    // The playhead (80s) must be inside the zoom window after a seek.
    expect(scale?.textContent).toBeTruthy();
    // Seek to 80 in a 100s session with a 60s window → {40, 60}, scale shows 0:40 … 1:40.
    expect(scale?.textContent).toContain('1:40');
  });
});

describe('navigation', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('next moves to the next session, wrapping at the end', () => {
    renderPlayer();
    fireEvent.click(screen.getByRole('button', { name: 'Next session' }));
    expect(screen.getByTestId('player-title').textContent).toBe('Session 2');
    fireEvent.click(screen.getByRole('button', { name: 'Next session' }));
    expect(screen.getByTestId('player-title').textContent).toBe('Session 3');
    fireEvent.click(screen.getByRole('button', { name: 'Next session' }));
    expect(screen.getByTestId('player-title').textContent).toBe('Session 1');
  });

  it('previous moves backwards, wrapping at the start', () => {
    renderPlayer();
    fireEvent.click(screen.getByRole('button', { name: 'Previous session' }));
    expect(screen.getByTestId('player-title').textContent).toBe('Session 3');
  });

  it('wires the ToggleFullscreen command to the client', () => {
    const client = mockClient();
    render(<PlayerView client={client} source={source} />);
    fireEvent.click(screen.getByRole('button', { name: 'Toggle fullscreen' }));
    expect(client.sent).toContainEqual({ method: 'ToggleFullscreen', parameters: { enabled: true } });
  });

  it('keyboard: space toggles play/pause', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    vi.spyOn(video, 'paused', 'get').mockReturnValue(true);
    const play = vi.spyOn(video, 'play').mockResolvedValue(undefined);
    act(() => {
      fireEvent.keyDown(window, { code: 'Space' });
    });
    expect(play).toHaveBeenCalledTimes(1);
  });

  it('keyboard: arrow keys seek', () => {
    const { container } = renderPlayer();
    act(() => {
      fireEvent.keyDown(window, { key: 'ArrowRight' });
    });
    expect(currentReadout()).toBe('0:05');
    const fill = container.querySelector('.timeline-bar-fill') as HTMLElement;
    expect(fill.style.width).toBe('5%');
  });

  it('a video play event updates the play state', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { configurable: true, value: 30 });
    act(() => {
      fireEvent.play(video);
    });
    expect(screen.getByRole('button', { name: 'Play or pause' }).textContent).toBe('Pause');
  });
});

describe('region selection seam (T9)', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('renders region marks on the zoomed timeline and reports selection', () => {
    const onRegionSelect = vi.fn();
    const { container } = render(
      <PlayerView
        client={mockClient()}
        source={source}
        regions={[{ id: 'r1', start: 20, end: 45 }]}
        selectedRegionId={null}
        onRegionSelect={onRegionSelect}
      />,
    );
    const region = container.querySelector('.timeline-region') as HTMLElement;
    expect(region).not.toBeNull();
    act(() => {
      fireEvent.click(region);
    });
    expect(onRegionSelect).toHaveBeenCalledWith({ id: 'r1', start: 20, end: 45 });
  });
});
