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
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    render(<PlayerView client={mockClient()} source={source} />);
    openClipDialog();
    let dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).getAllByRole('button', { name: /(Select|Deselect) region 1/ }).length).toBe(1);
    fireEvent.click(within(dialog).getByRole('button', { name: 'Remove region 1' }));
    dialog = screen.getByRole('dialog', { name: 'Create clip' });
    expect(within(dialog).queryByRole('button', { name: /(Select|Deselect) region/ })).toBeNull();
    expect(within(dialog).getByText(/No regions yet/)).toBeTruthy();
  });

  it('the region list shows each region with start/end', () => {
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    const { container } = render(<PlayerView client={client} source={source} />);
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
    const { container } = render(<PlayerView client={client} source={source} />);
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
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
    openClipDialog();
    setVideoTime(container, 47);
    expect(currentReadout()).toBe('0:47');
  });

  it('clicking a segment on the timeline toggles it as the loop target', () => {
    const { container } = render(<PlayerView client={mockClient()} source={source} />);
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
    render(<PlayerView client={client} source={source} />);
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
    render(<PlayerView client={client} source={source} />);
    openClipDialog();
    fireEvent.click(createButton());
    act(() => {
      client.emit('importProgress', { status: 'error', error: 'encoder failed' });
    });
    expect(screen.getByTestId('clip-progress-error').textContent).toContain('encoder failed');
  });

  it('the state message audio tracks surface per-track controls in the dialog', () => {
    const client = mockClient();
    render(<PlayerView client={client} source={source} />);
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
