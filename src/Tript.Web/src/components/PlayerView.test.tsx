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

/**
 * The zoom window's width in seconds, derived from the scale row the timeline shows while zoomed.
 * The player used to print "Zoom window: 48.0s" in its footer, which read as debug output on screen.
 */
function zoomWindowSeconds(container: Element): number {
  const scale = container.querySelector('.timeline-scale');
  const [start, end] = [...(scale?.querySelectorAll('span') ?? [])].map((span) => {
    const [minutes, seconds] = (span.textContent ?? '0:00').split(':');
    return Number(minutes) * 60 + Number(seconds);
  });
  return end - start;
}

/** Which item the player is showing, read off the video's accessible name. */
function playingItem(): string {
  return (document.querySelector('video')?.getAttribute('aria-label') ?? '').split(' — ')[0];
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
    expect(playingItem()).toBe('Session 1');
    expect(container.querySelector('.timeline-bar')).not.toBeNull();
    expect(container.querySelector('.timeline-zoomed')).not.toBeNull();
    expect(screen.getByTestId('transport-duration').textContent).toBe('1:40');
  });

  it('renders the bookmark ticks on the full-session bar', () => {
    renderPlayer();
    expect(screen.getAllByRole('button', { name: /^Bookmark at / })).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Bookmark at 20.0s' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Bookmark at 80.0s' })).toBeTruthy();
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
    expect(video.poster).toContain('/api/thumbnail/sessions/a.mp4');
    expect(screen.getByRole('button', { name: 'Play recording' })).toBeTruthy();
  });

  it('starts videos automatically when the player opens', () => {
    renderPlayer();
    expect((document.querySelector('video') as HTMLVideoElement).autoplay).toBe(true);
  });

  it('resets the playhead when navigating to another session', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 80));
    expect(currentReadout()).toBe('1:20');
    fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    expect(playingItem()).toBe('Session 2');
    expect(currentReadout()).toBe('0:00');
  });

  it('navigates the library result set when one is provided', () => {
    const clip = { ...session(9, 'clips/result.mp4'), contentType: 'clip' as const, title: 'Result clip' };
    render(
      <PlayerView
        client={mockClient()}
        source={source}
        item={clip}
        navigationItems={[clip, session(2, 'sessions/b.mp4')]}
      />,
    );
    expect(playingItem()).toBe('Result clip');
    fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    expect(playingItem()).toBe('Session 2');
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
    renderPlayer();
    act(() => {
      fireEvent.pointerDown(screen.getByRole('button', { name: 'Bookmark at 20.0s' }), { clientX: 20, pointerId: 1 });
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
    expect(zoomWindowSeconds(container)).toBe(48);
  });

  it('a wheel zoom-out grows the visible window back', () => {
    const { container } = renderPlayer();
    act(() => clickZoomedAt(container, 36, 0, 60));
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: 100 });
    // 48s × 1.25 = 60s window.
    expect(zoomWindowSeconds(container)).toBe(60);
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
    fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    expect(playingItem()).toBe('Session 2');
    fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    expect(playingItem()).toBe('Session 3');
    fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    expect(playingItem()).toBe('Session 1');
  });

  it('previous moves backwards, wrapping at the start', () => {
    renderPlayer();
    fireEvent.click(screen.getByRole('button', { name: 'Previous recording' }));
    expect(playingItem()).toBe('Session 3');
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
    expect(screen.getByRole('button', { name: 'Pause' })).toBeTruthy();
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
  it('does not let the browser paint its controls over the video', () => {
    renderPlayer();
    const video = document.querySelector('video');
    expect(video).not.toBeNull();
    expect(video?.hasAttribute('controls')).toBe(false);
  });

  it('keeps the video reachable by keyboard now that controls no longer focus it', () => {
    renderPlayer();
    expect(document.querySelector('video')?.getAttribute('tabindex')).toBe('0');
  });

  it('applies the transport volume to the media element', () => {
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Volume'), { target: { value: '0.25' } });
    });
    expect((document.querySelector('video') as HTMLVideoElement).volume).toBeCloseTo(0.25);
  });

  it('recovers sound after the slider is dragged to zero and then unmuted', () => {
    // Dragging to zero mutes. Unmuting from there used to leave volume at zero: the button read
    // "Mute" again, the slider sat at zero, and nothing played.
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Volume'), { target: { value: '0.7' } });
    });
    act(() => {
      fireEvent.change(screen.getByLabelText('Volume'), { target: { value: '0' } });
    });
    expect(screen.getByRole('button', { name: 'Unmute' })).toBeTruthy();
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Unmute' }));
    });
    const video = document.querySelector('video') as HTMLVideoElement;
    expect(video.muted).toBe(false);
    expect(video.volume).toBeGreaterThan(0);
    expect(video.volume).toBeCloseTo(0.7);
  });

  it('applies a chosen playback speed to the media element', () => {
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Playback speed'), { target: { value: '0.5' } });
    });
    expect((document.querySelector('video') as HTMLVideoElement).playbackRate).toBeCloseTo(0.5);
  });

  it('keeps the chosen speed when moving to another session', () => {
    // playbackRate resets to defaultPlaybackRate whenever a source loads, so unlike volume it is
    // not enough to set it once — both have to be written or the speed silently returns to 1x.
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Playback speed'), { target: { value: '2' } });
    });
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Next recording' }));
    });
    const video = document.querySelector('video') as HTMLVideoElement;
    expect(video.defaultPlaybackRate).toBeCloseTo(2);
    expect(video.playbackRate).toBeCloseTo(2);
  });

  it('names the video so a keyboard user landing on it knows what it is', () => {
    renderPlayer();
    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('play or pause');
  });

  it('mutes and unmutes the media element, restoring the chosen level', () => {
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Volume'), { target: { value: '0.6' } });
    });
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Mute' }));
    });
    const video = document.querySelector('video') as HTMLVideoElement;
    expect(video.muted).toBe(true);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Unmute' }));
    });
    expect(video.muted).toBe(false);
    expect(video.volume).toBeCloseTo(0.6);
  });
});
