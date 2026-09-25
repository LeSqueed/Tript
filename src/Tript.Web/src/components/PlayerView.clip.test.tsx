// SPDX-License-Identifier: GPL-2.0-or-later

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

function openClipDialog(): void {
  const button = screen.queryByRole('button', { name: 'Open clip dialog' }) as HTMLButtonElement | null;
  if (!button || button.disabled) {
    const video = document.querySelector('video') as HTMLVideoElement | null;
    if (video && Number.isFinite(video.duration) && video.duration > 0) {
      act(() => {
        fireEvent.click(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' }));
      });
    }
  }
  act(() => {
    const current = screen.queryByRole('button', { name: 'Open clip dialog' }) as HTMLButtonElement | null;
    if (current && !current.disabled) {
      fireEvent.click(current);
    }
  });
}

function createButton(): HTMLElement {
  const dialog = screen.getByRole('dialog', { name: 'Create clip' });
  return within(dialog).getByRole('button', { name: /^Create (clip|1 clip)/ });
}

function setVideoDuration(container: HTMLElement, seconds: number): void {
  const video = container.querySelector('video') as HTMLVideoElement;
  act(() => {
    Object.defineProperty(video, 'duration', { configurable: true, writable: true, value: seconds });
    fireEvent.durationChange(video);
  });
}

function renderPlayer(
  client: IpcClient = mockClient(),
  mediaSeconds = 100,
  sessionSource: SessionSource = source,
): HTMLElement {
  const { container } = render(<PlayerView client={client} source={sessionSource} />);
  setVideoDuration(container, mediaSeconds);
  return container;
}

function setVideoTime(container: HTMLElement, time: number): void {
  const video = container.querySelector('video') as HTMLVideoElement;
  act(() => {
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: time });
    fireEvent.timeUpdate(video);
  });
}

function seekTo(container: HTMLElement, time: number): void {
  const bar = container.querySelector('.timeline-bar') as Element;
  fireEvent.pointerDown(bar, { clientX: time, pointerId: 1 });
  fireEvent.pointerUp(bar, { clientX: time, pointerId: 1 });
}

function regionLabels(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll('.timeline-region')).map(
    (element) => element.getAttribute('aria-label') ?? '',
  );
}

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

function markDefaultSegment(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' }));
  });
}

describe('clip dialog: default region from the playbar', () => {
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
    seekTo(container, 42);
    expect(currentReadout()).toBe('0:42');
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(dialog).toBeTruthy();
    const row = within(dialog).getByRole('button', { name: /Deselect clip 1/ });
    expect(row.textContent).toContain('0:37 – 0:47');
  });

  it('the default region stays clamped to the session at the playhead edges', () => {
    const container = renderPlayer();
    seekTo(container, 98);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const row = within(dialog).getByRole('button', { name: /Deselect clip 1/ });
    expect(row.textContent).toContain('1:30 – 1:40');
  });
});

describe('clip dialog: region list in the player', () => {
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
    expect(within(dialog).getAllByRole('button', { name: /(Select|Deselect) clip 1/ }).length).toBe(1);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove region 1' }));
    dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).queryByRole('button', { name: /(Select|Deselect) region/ })).toBeNull();
    expect(within(dialog).getByText(/No clips yet/)).toBeTruthy();
  });

  it('the region list shows each region with start/end', () => {
    const container = renderPlayer();
    const bar = container.querySelector('.timeline-bar') as Element;
    fireEvent.pointerDown(bar, { clientX: 42, pointerId: 1 });
    fireEvent.pointerUp(bar, { clientX: 42, pointerId: 1 });
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getByRole('button', { name: /Deselect clip 1/ }).textContent).toContain(
      '0:37 – 0:47',
    );
  });
});

describe('clip dialog: combine vs separate payloads through the player', () => {
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
    fireEvent.click(screen.getByRole('radio', { name: 'Separate clips' }));
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
    expect(screen.getByRole('button', { name: 'Set the clip end' })).toHaveProperty('disabled', true);
  });

  it('the in/out buttons mark a segment between the two playhead positions', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Set the clip start' }));
    });
    expect(screen.getByTestId('timeline-mark-in')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Set the clip end' })).toHaveProperty('disabled', false);
    seekTo(container, 40);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Set the clip end' }));
    });
    expect(regionLabels(container)).toEqual(['Region 0:20–0:40']);
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    expect(screen.getByTestId('player-clip-hint').textContent).toMatch(/1 clip ready/);
  });

  it('closing a segment on the same frame marks nothing and keeps the in point standing', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Set the clip start' }));
      fireEvent.click(screen.getByRole('button', { name: 'Set the clip end' }));
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
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    act(() => {
      fireEvent.keyDown(window, { key: 'i' });
    });
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Close clip dialog' }));
    act(() => {
      fireEvent.keyDown(window, { key: 'i' });
    });
    expect(screen.getByTestId('timeline-mark-in')).toBeTruthy();
  });

  it('I/O/M stand down while a button owns keyboard focus', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    const markInButton = screen.getByRole('button', { name: 'Set the clip start' });
    markInButton.focus();
    fireEvent.keyDown(markInButton, { key: 'i' });
    fireEvent.keyDown(markInButton, { key: 'm' });
    fireEvent.keyDown(markInButton, { key: 'o' });
    expect(screen.queryByTestId('timeline-mark-in')).toBeNull();
    expect(regionLabels(container)).toEqual([]);
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
    expect(within(dialog).getAllByRole('button', { name: /(Select|Deselect) clip \d/ })).toHaveLength(2);
    expect(within(dialog).getByRole('button', { name: /(Select|Deselect) clip 1/ }).textContent).toContain(
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
    act(() => {
      fireEvent.click(screen.getByRole('radio', { name: 'Separate clips' }));
    });
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

  function markedPlayer(): HTMLElement {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    return container;
  }

  it('dragging the body slides the segment and preserves its length', () => {
    const container = markedPlayer();
    dragRegion(container, 'body', 37, 43);
    expect(regionLabels(container)).toEqual(['Region 0:43–0:53']);
  });

  it('dragging the body past the session end clamps the whole segment, keeping its length', () => {
    const container = markedPlayer();
    dragRegion(container, 'body', 50, 250);
    expect(regionLabels(container)).toEqual(['Region 1:30–1:40']);
  });

  it('dragging the left edge moves only the start', () => {
    const container = markedPlayer();
    dragRegion(container, 'start', 37, 42);
    expect(regionLabels(container)).toEqual(['Region 0:42–0:47']);
  });

  it('dragging the right edge moves only the end', () => {
    const container = markedPlayer();
    dragRegion(container, 'end', 47, 54);
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
    dragRegion(container, 'end', 47, 54);
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
    dragRegion(container, 'end', 47, 54);
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const endField = within(dialog).getByLabelText('Clip 1 end, seconds') as HTMLInputElement;
    expect(Number(endField.value)).toBe(54);
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
    seekTo(container, 42);
    const video = container.querySelector('video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { configurable: true, writable: true, value: 42 });
    act(() => {
      fireEvent.play(video);
    });
    openClipDialog();
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
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
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
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
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getByRole('button', { name: /Deselect clip 1/ })).toBeTruthy();
    const regionButton = container.querySelector('.timeline-region') as HTMLElement;
    expect(regionButton).not.toBeNull();
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(within(dialog).queryByRole('button', { name: /Deselect clip 1/ })).toBeNull();
  });

  it('clicking a segment moves the playhead to its start; clicking again to deselect does not', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:37–0:47']);
    const regionButton = container.querySelector('.timeline-region') as HTMLElement;
    expect(regionButton.className).toContain('selected');
    act(() => {
      fireEvent.click(regionButton);
    });
    expect(regionButton.className).not.toContain('selected');
    expect(currentReadout()).toBe('0:42');
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
    expect(screen.getByRole('button', { name: 'Play' })).toBeTruthy();
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
    setVideoTime(container, 46);
    expect(currentReadout()).toBe('0:46');
    dragRegion(container, 'end', 47, 43);
    expect(regionLabels(container)).toEqual(['Region 0:37–0:43']);
    expect(currentReadout()).toBe('0:37');
  });

  it('dragging the start past the playhead brings the playhead with it', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    dragRegion(container, 'start', 37, 45);
    expect(regionLabels(container)).toEqual(['Region 0:45–0:47']);
    expect(currentReadout()).toBe('0:45');
  });

  it('dragging the start earlier leaves the playhead alone', () => {
    const container = renderPlayer();
    seekTo(container, 42);
    markDefaultSegment();
    dragRegion(container, 'start', 37, 24);
    expect(regionLabels(container)).toEqual(['Region 0:24–0:47']);
    expect(currentReadout()).toBe('0:42');
  });

  it('editing a segment that is not the loop target never moves the playhead', () => {
    const container = renderPlayer();
    seekTo(container, 20);
    markDefaultSegment();
    seekTo(container, 60);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:15–0:25', 'Region 0:55–1:05']);
    seekTo(container, 20);
    dragRegion(container, 'start', 25, 40);
    expect(regionLabels(container)).toEqual(['Region 0:24–0:25', 'Region 0:55–1:05']);
    expect(currentReadout()).toBe('0:20');
  });
});

describe('clip creation handoff', () => {
  beforeEach(() => {
    stubLayout();
    vi.useFakeTimers();
  });
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('hands clip requests to the shell when it owns the queue', () => {
    const client = mockClient();
    const requests: { id: string; title: string }[] = [];
    const { container } = render(
      <PlayerView
        client={client}
        source={source}
        onCreateClip={(parameters) => requests.push(parameters)}
      />,
    );
    setVideoDuration(container, 100);
    markDefaultSegment();
    openClipDialog();
    fireEvent.click(createButton());
    expect(requests).toHaveLength(1);
    expect(client.sent.filter((entry) => entry.method === 'CreateClip')).toHaveLength(0);
  });

  it('no longer renders a per-clip progress list under the player', () => {
    const client = mockClient();
    const container = renderPlayer(client);
    markDefaultSegment();
    openClipDialog();
    fireEvent.click(createButton());
    const sent = client.sent.find((entry) => entry.method === 'CreateClip')?.parameters as { id: string };
    act(() => {
      client.emit('importProgress', { id: sent.id, status: 'done', content: {} });
    });
    expect(container.querySelector('.player-clip-progress')).toBeNull();
    expect(screen.queryByText('Creating…')).toBeNull();
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
    markDefaultSegment();
    openClipDialog();
    expect(screen.getByText('Game audio')).toBeTruthy();
    expect(screen.getByLabelText('Game audio volume')).toBeTruthy();
  });
});

function markInAtPlayhead(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Set the clip start' }));
  });
}

function markOutAtPlayhead(): void {
  act(() => {
    fireEvent.click(screen.getByRole('button', { name: 'Set the clip end' }));
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
    const container = renderPlayer(client);
    seekTo(container, 5);
    markInAtPlayhead();
    seekTo(container, 15);
    markOutAtPlayhead();
    seekTo(container, 42);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:05–0:15', 'Region 0:37–0:47']);

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
    expect(regionLabels(container)).toEqual(['Region 0:00–0:03']);
    openClipDialog();
    fireEvent.click(createButton());
    const params = (client.sent.filter((c) => c.method === 'CreateClip')[0].parameters ?? {}) as Record<
      string,
      unknown
    >;
    expect(params.segments).toEqual([{ startTime: 0, endTime: 3 }]);
  });

  it('does not open clip creation before a segment is marked', () => {
    renderPlayer(mockClient(), 3);
    expect(screen.queryByRole('dialog', { name: 'Create clip' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open clip dialog' })).toBeNull();
  });

  it('an out-of-bounds segment cannot be typed in the dialog either', () => {
    const container = renderPlayer(mockClient(), 8);
    markDefaultSegment();
    openClipDialog();
    const dialog = screen.getByRole('dialog', { name: 'Create clip' });
    const endField = within(dialog).getByLabelText('Clip 1 end, seconds') as HTMLInputElement;
    fireEvent.change(endField, { target: { value: '95' } });
    fireEvent.blur(endField);
    expect(regionLabels(container)).toEqual(['Region 0:00–0:08']);
  });

  it('a dragged segment cannot be pulled past the real end', () => {
    const container = renderPlayer(mockClient(), 8);
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:00–0:08']);
    dragRegion(container, 'end', 90, 400);
    const labels = regionLabels(container);
    expect(labels).toHaveLength(1);
    expect(labels[0]).toBe('Region 0:00–0:08');
  });

  it('refuses all three marking gestures while only the DECLARED length is known', () => {
    const realSeconds = 9.13;
    const client = mockClient();
    const { container } = render(<PlayerView client={client} source={source} />);
    seekTo(container, 95);
    expect(currentReadout()).toBe('1:35');

    expect(screen.getByRole('button', { name: 'Set the clip start' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Set the clip end' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' })).toHaveProperty(
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

    openClipDialog();
    expect(screen.queryByRole('dialog', { name: 'Create clip' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open clip dialog' })).toBeNull();
    expect(client.sent.filter((c) => c.method === 'CreateClip')).toHaveLength(0);

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
    const noLength: SessionSource = {
      getSessions: () => [{ ...session(1, 'sessions/a.mp4'), endTime: undefined }],
      getBookmarks: () => [],
    };
    const client = mockClient();
    const { container } = render(<PlayerView client={client} source={noLength} />);
    expect(screen.getByRole('button', { name: 'Set the clip start' })).toHaveProperty('disabled', true);
    expect(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' })).toHaveProperty(
      'disabled',
      true,
    );
    expect(screen.getByTestId('player-clip-hint').textContent).toMatch(/Waiting for the video length/);
    act(() => {
      fireEvent.keyDown(window, { key: 'm' });
    });
    expect(regionLabels(container)).toEqual([]);
    openClipDialog();
    expect(screen.queryByRole('dialog', { name: 'Create clip' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Open clip dialog' })).toBeNull();
    expect(client.sent.filter((c) => c.method === 'CreateClip')).toHaveLength(0);

    setVideoDuration(container, 12);
    expect(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' })).toHaveProperty(
      'disabled',
      false,
    );
    markDefaultSegment();
    expect(regionLabels(container)).toEqual(['Region 0:00–0:10']);
  });
});
