// SPDX-License-Identifier: GPL-2.0-or-later

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

function zoomWindowSeconds(container: Element): number {
  const scale = container.querySelector('.timeline-scale');
  const [start, end] = [...(scale?.querySelectorAll('span') ?? [])].map((span) => {
    const [minutes, seconds] = (span.textContent ?? '0:00').split(':');
    return Number(minutes) * 60 + Number(seconds);
  });
  return end - start;
}

function playingItem(): string {
  return (document.querySelector('video')?.getAttribute('aria-label') ?? '').split('. Press space')[0];
}

function currentReadout(): string {
  return screen.getByTestId('transport-current').textContent ?? '';
}

function stubLayout(): void {
  Element.prototype.getBoundingClientRect = () =>
    ({ left: 0, top: 0, right: 100, bottom: 44, width: 100, height: 44 }) as DOMRect;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
}

function clickBarAt(container: HTMLElement, time: number): void {
  const element = container.querySelector('.timeline-bar');
  expect(element).not.toBeNull();
  fireEvent.pointerDown(element as Element, { clientX: time, pointerId: 1 });
  fireEvent.pointerUp(element as Element, { clientX: time, pointerId: 1 });
}

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
    expect(container.querySelector('.timeline-scale')).toBeNull();
    expect(screen.getByTestId('transport-duration').textContent).toBe('1:40');
  });

  it('renders the bookmark ticks on the full-session bar', () => {
    renderPlayer();
    expect(screen.getAllByRole('button', { name: /^(Kill|Goal) at / })).toHaveLength(2);
    expect(screen.getByRole('button', { name: 'Kill at 0:20' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Goal at 1:20' })).toBeTruthy();
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
    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
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
    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
    expect(playingItem()).toBe('Session 2');
  });

  it('shows position in the review set and favorites the current item', () => {
    const onToggleFavorite = vi.fn();
    const current = { ...session(2, 'sessions/b.mp4'), favorite: true };
    render(
      <PlayerView
        client={mockClient()}
        source={source}
        item={current}
        navigationItems={[session(1, 'sessions/a.mp4'), current, session(3, 'sessions/c.mp4')]}
        onToggleFavorite={onToggleFavorite}
      />,
    );

    expect(screen.getByLabelText('Item 2 of 3').textContent).toBe('2 of 3');
    fireEvent.click(screen.getByRole('button', { name: 'Remove from favorites' }));
    expect(onToggleFavorite).toHaveBeenCalledWith(current);
  });

  it('shows the navigation list as a playlist and selects an item from it', () => {
    const current = session(1, 'sessions/a.mp4');
    render(
      <PlayerView
        client={mockClient()}
        source={source}
        item={current}
        navigationItems={[current, session(2, 'sessions/b.mp4')]}
      />,
    );

    expect(screen.getByRole('complementary', { name: 'Playlist and bookmarks' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Playing Session 1' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Play Session 2' }));
    expect(playingItem()).toBe('Session 2');
  });

  it('toggles favorite with F while player shortcuts are active', () => {
    const onToggleFavorite = vi.fn();
    const current = session(1, 'sessions/a.mp4');
    render(
      <PlayerView
        client={mockClient()}
        source={source}
        item={current}
        navigationItems={[current]}
        onToggleFavorite={onToggleFavorite}
      />,
    );

    fireEvent.keyDown(window, { key: 'f' });
    expect(onToggleFavorite).toHaveBeenCalledWith(current);
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
    act(() => clickZoomedAt(container, 36));
    expect(currentReadout()).toBe('0:36');
    const fill = container.querySelector('.timeline-bar-fill') as HTMLElement;
    expect(fill.style.width).toBe('36%');
  });

  it('moves the playhead on the full-session bar and the zoomed timeline follows', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 42));
    const track = container.querySelector('.timeline-track') as HTMLElement;
    expect(track).not.toBeNull();
    const playhead = track.querySelector('.timeline-playhead') as HTMLElement;
    expect(playhead).not.toBeNull();
    expect(playhead.style.left).toBe('42px');
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
      fireEvent.pointerDown(screen.getByRole('button', { name: 'Kill at 0:20' }), { clientX: 20, pointerId: 1 });
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

describe('bookmark panel and manual bookmarks', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  function renderWith(item: ContentItem, client = mockClient()) {
    const result = render(
      <PlayerView client={client} source={source} item={item} navigationItems={[item]} />,
    );
    return { ...result, client };
  }

  it('offers a bookmarks tab only when the recording has bookmarks', () => {
    const { unmount } = renderWith(session(1, 'sessions/a.mp4'));
    expect(screen.getByRole('tab', { name: 'Bookmarks' })).toBeTruthy();
    unmount();

    renderWith(session(2, 'sessions/b.mp4'));
    expect(screen.queryByRole('tab', { name: 'Bookmarks' })).toBeNull();
  });

  it('unticking a type clears its markers from both timelines', () => {
    const { container } = renderWith(session(1, 'sessions/a.mp4'));
    fireEvent.click(screen.getByRole('tab', { name: 'Bookmarks' }));

    expect(container.querySelectorAll('.timeline-bookmark')).toHaveLength(2);
    expect(container.querySelectorAll('.timeline-tick')).toHaveLength(2);

    fireEvent.click(screen.getByRole('checkbox', { name: 'Show Kill bookmarks' }));

    expect(container.querySelectorAll('.timeline-bookmark')).toHaveLength(1);
    expect(container.querySelectorAll('.timeline-tick')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Goal at 1:20' })).toBeTruthy();
  });

  it('B sends a manual bookmark at the current time', () => {
    const { container, client } = renderWith(session(2, 'sessions/b.mp4'));
    act(() => clickBarAt(container, 42));

    fireEvent.keyDown(window, { key: 'b' });

    expect(client.sent).toContainEqual({
      method: 'AddBookmark',
      parameters: {
        contentType: 'recording',
        filePath: 'sessions/b.mp4',
        id: '',
        time: 42,
        type: 'manual',
      },
    });
  });

  it('does not bookmark a clip, which has nowhere to store one', () => {
    const clip: ContentItem = {
      ...session(1, 'clips/a.mp4'),
      contentType: 'clip',
    };
    const { client } = renderWith(clip);

    fireEvent.keyDown(window, { key: 'b' });

    expect(client.sent.some((message) => message.method === 'AddBookmark')).toBe(false);
    expect(screen.queryByRole('button', { name: 'Add a bookmark where you are' })).toBeNull();
  });

  it('deletes a manual bookmark by id', () => {
    const item = session(3, 'sessions/manual.mp4');
    const manualSource: SessionSource = {
      getSessions: () => [item],
      getBookmarks: () => [{ id: 'm1', type: 'manual', time: 30 }],
    };
    const client = mockClient();
    render(<PlayerView client={client} source={manualSource} item={item} navigationItems={[item]} />);

    fireEvent.click(screen.getByRole('button', { name: 'Remove the bookmark at 0:30' }));

    expect(client.sent).toContainEqual({
      method: 'DeleteBookmark',
      parameters: { contentType: 'recording', filePath: 'sessions/manual.mp4', id: 'm1' },
    });
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
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    expect(zoomWindowSeconds(container)).toBe(80);
  });

  it('stays fully zoomed out when the media reports a longer real duration', () => {
    const { container } = renderPlayer();
    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'duration', { configurable: true, value: 240 });
    fireEvent.durationChange(video);

    expect(screen.getByTestId('transport-duration').textContent).toBe('4:00');
    expect(container.querySelector('.timeline-scale')).toBeNull();
  });

  it('preserves a user zoom when the real duration arrives', () => {
    const { container } = renderPlayer();
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    expect(zoomWindowSeconds(container)).toBe(80);

    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'duration', { configurable: true, value: 240 });
    fireEvent.durationChange(video);

    expect(zoomWindowSeconds(container)).toBe(80);
  });

  it('a wheel zoom-out grows the visible window back', () => {
    const { container } = renderPlayer();
    const zoomed = container.querySelector('.timeline-zoomed') as Element;
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: -100 });
    fireEvent.wheel(zoomed, { clientX: 40, deltaY: 100 });
    expect(container.querySelector('.timeline-scale')).toBeNull();
  });

  it('a full-session bar seek recentres the zoom window on the playhead', () => {
    const { container } = renderPlayer();
    act(() => clickBarAt(container, 80));
    const playhead = container.querySelector('.timeline-track .timeline-playhead') as HTMLElement;
    expect(playhead.style.left).toBe('80px');
    expect(container.querySelector('.timeline-scale')).toBeNull();
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

  it('next moves forward and is disabled at the last session', () => {
    renderPlayer();
    const previous = screen.getByRole('button', { name: 'Previous item' }) as HTMLButtonElement;
    const next = screen.getByRole('button', { name: 'Next item' }) as HTMLButtonElement;
    expect(previous.disabled).toBe(true);
    expect(next.disabled).toBe(false);
    fireEvent.click(next);
    expect(playingItem()).toBe('Session 2');
    expect(previous.disabled).toBe(false);
    fireEvent.click(next);
    expect(playingItem()).toBe('Session 3');
    expect(next.disabled).toBe(true);
    fireEvent.click(next);
    expect(playingItem()).toBe('Session 3');
  });

  it('previous moves backward and is disabled at the first session', () => {
    renderPlayer();
    const previous = screen.getByRole('button', { name: 'Previous item' }) as HTMLButtonElement;
    const next = screen.getByRole('button', { name: 'Next item' }) as HTMLButtonElement;
    fireEvent.click(next);
    expect(playingItem()).toBe('Session 2');
    fireEvent.click(previous);
    expect(playingItem()).toBe('Session 1');
    expect(previous.disabled).toBe(true);
    fireEvent.click(previous);
    expect(playingItem()).toBe('Session 1');
  });

  it('toggles fullscreen in the browser without involving the host', () => {
    const client = mockClient();
    const requestFullscreen = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(document, 'fullscreenEnabled', { configurable: true, value: true });
    const original = HTMLElement.prototype.requestFullscreen;
    HTMLElement.prototype.requestFullscreen = requestFullscreen;
    try {
      render(<PlayerView client={client} source={source} />);
      fireEvent.click(screen.getByRole('button', { name: 'Toggle fullscreen' }));
      expect(requestFullscreen).toHaveBeenCalledTimes(1);
      expect(client.sent).toEqual([]);
    } finally {
      HTMLElement.prototype.requestFullscreen = original;
      Reflect.deleteProperty(document, 'fullscreenEnabled');
    }
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

  it('pointer actions release focus so the next Space controls playback', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    vi.spyOn(video, 'paused', 'get').mockReturnValue(true);
    const play = vi.spyOn(video, 'play').mockResolvedValue(undefined);
    const fullscreen = screen.getByRole('button', { name: 'Toggle fullscreen' });
    fullscreen.focus();
    fireEvent.pointerUp(fullscreen);
    expect(document.activeElement).not.toBe(fullscreen);

    fireEvent.keyDown(window, { code: 'Space' });
    expect(play).toHaveBeenCalledTimes(1);
  });

  it('Space stands down while a button owns keyboard focus', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    vi.spyOn(video, 'paused', 'get').mockReturnValue(true);
    const play = vi.spyOn(video, 'play').mockResolvedValue(undefined);
    const fullscreen = screen.getByRole('button', { name: 'Toggle fullscreen' });
    fullscreen.focus();

    fireEvent.keyDown(fullscreen, { code: 'Space' });
    expect(document.activeElement).toBe(fullscreen);
    expect(play).not.toHaveBeenCalled();
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

  it('keyboard: Shift+arrows navigate without replacing plain-arrow seeking', () => {
    renderPlayer();
    fireEvent.keyDown(window, { key: 'ArrowRight', shiftKey: true });
    expect(playingItem()).toBe('Session 2');
    expect(currentReadout()).toBe('0:00');

    fireEvent.keyDown(window, { key: 'ArrowLeft', shiftKey: true });
    expect(playingItem()).toBe('Session 1');
  });

  it('preserves paused state while navigating', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    const play = vi.spyOn(video, 'play').mockResolvedValue(undefined);

    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
    expect(play).not.toHaveBeenCalled();
  });

  it('preserves playing state while navigating', () => {
    renderPlayer();
    const video = document.querySelector('video') as HTMLVideoElement;
    fireEvent.play(video);
    const play = vi.spyOn(video, 'play').mockResolvedValue(undefined);

    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
    expect(play).toHaveBeenCalledOnce();
  });

  it('keyboard: Delete trashes the current item once', () => {
    const onDelete = vi.fn();
    const current = session(1, 'sessions/a.mp4');
    render(
      <PlayerView
        client={mockClient()}
        source={source}
        item={current}
        navigationItems={[current]}
        onDelete={onDelete}
      />,
    );

    fireEvent.keyDown(window, { key: 'Delete' });
    fireEvent.keyDown(window, { key: 'Delete', repeat: true });
    expect(onDelete).toHaveBeenCalledOnce();
    expect(onDelete).toHaveBeenCalledWith(current);
  });

  it('Escape clears a pending clip start before leaving the player', () => {
    const onBack = vi.fn();
    render(<PlayerView client={mockClient()} source={source} onBack={onBack} />);
    const video = document.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'duration', { configurable: true, value: 100 });
    fireEvent.durationChange(video);
    fireEvent.keyDown(window, { key: 'i' });
    expect(screen.getByRole('button', { name: 'Clear the clip start' })).toBeTruthy();

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(screen.queryByRole('button', { name: 'Clear the clip start' })).toBeNull();
    expect(onBack).not.toHaveBeenCalled();

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onBack).toHaveBeenCalledOnce();
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
    renderPlayer();
    act(() => {
      fireEvent.change(screen.getByLabelText('Playback speed'), { target: { value: '2' } });
    });
    act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
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
