// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shell render test: the recorder bar, nav and theme render; navigation switches views; the
// connection state is shown even when the backend is not running (fails gracefully). The IPC
// client is mocked at the socket level so the real client logic runs against a test double.
//
// The nav is two routes now — `[ Library ] [ Settings ]`. Player and Clips are deliberately NOT
// routes: the clips list folded into the library's type filter, and the player is an overlay raised
// over the library. The nav assertions below are scoped to the nav landmark rather than to the whole
// document, because "Clips" still exists as a *filter* button inside the library — asserting its
// absence document-wide would assert the filter away with it.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, act, within } from '@testing-library/react';
import { App } from './App';
import { MockWebSocket } from '../ipc/test/mockWebSocket';

/** The active socket — under StrictMode the effect runs twice, so the app's live socket is last. */
function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

/**
 * The backend's content-list answer to a ListContent command: two recordings and a clip, which is
 * the shape the library's type filter has to deal with. `SESSION_2` carries a `game` and a real
 * `startTime`; `SESSION_1` carries neither (the older/no-metadata shape) — both must show up.
 */
const SESSION_1 = {
  contentType: 'recording',
  fileName: 'session-1.mp4',
  filePath: 'sessions/2026-08-01/session-1.mp4',
  title: 'Session 1',
  startTime: 0,
  endTime: 120,
};

const SESSION_2 = {
  contentType: 'recording',
  fileName: 'session-2.mp4',
  filePath: 'sessions/2026-08-02/session-2.mp4',
  title: 'Session 2',
  game: 'Counter-Strike 2',
  startTime: 1786983000,
  endTime: 90,
};

const CLIP_1 = {
  contentType: 'clip',
  fileName: 'clip-1.mp4',
  filePath: 'clips/clip-1.mp4',
  title: 'Nice shot',
  game: 'Rocket League',
  startTime: 1786983889,
  durationSeconds: 3.083333,
  fileSizeBytes: 33338,
};

/**
 * A mock backend that honours the ListContent contract: the backend does NOT include content in its
 * NewConnection push, it answers every ListContent command with a `content` push. The frontend's IPC
 * session sources send ListContent on creation (and again on (re)connect), so this round-trip is
 * what surfaces sessions and clips in the library / player.
 */
class ContentBackend extends MockWebSocket {
  send(data: string): void {
    super.send(data);
    const frame = JSON.parse(data) as { method?: string };
    if (frame.method === 'ListContent') {
      this.serverMessage(
        JSON.stringify({
          method: 'content',
          content: { content: [CLIP_1, SESSION_2, SESSION_1] },
        }),
      );
    }
  }
}

describe('App shell', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  function renderApp() {
    const factory = (url: string) => new ContentBackend(url);
    const result = render(<App ipcOptions={{ createSocket: factory }} />);
    return { ...result, factory };
  }

  it('renders the recorder bar and shows connecting before the backend answers', () => {
    renderApp();
    expect(screen.getByText('Stopped')).toBeTruthy();
    expect(screen.getByTestId('connection-state').textContent).toBe('Connecting…');
  });

  /** Open the socket so ListContent reaches the mock backend and its `content` push comes back. */
  function connect(): void {
    act(() => {
      activeSocket().serverOpen();
    });
  }

  it('renders the nav and switches views', () => {
    renderApp();
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getByRole('button', { name: 'Library' })).toBeTruthy();
    expect(within(nav).getByRole('button', { name: 'Settings' })).toBeTruthy();

    connect();
    // The library grid carries both content types now — the clips page is gone.
    expect(screen.getByRole('button', { name: 'Open Session 1' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Nice shot' })).toBeTruthy();

    fireEvent.click(within(nav).getByRole('button', { name: 'Settings' }));
    expect(screen.getByRole('tab', { name: 'Recording' })).toBeTruthy();

    fireEvent.click(within(nav).getByRole('button', { name: 'Library' }));
    expect(screen.getByTestId('library-grid')).toBeTruthy();
  });

  it('the nav no longer offers Player or Clips as routes', () => {
    renderApp();
    connect();
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    // Exactly two routes, and neither of the two that were folded into the library.
    expect(within(nav).getAllByRole('button').map((button) => button.textContent)).toEqual([
      'Library',
      'Settings',
    ]);
    expect(within(nav).queryByRole('button', { name: 'Player' })).toBeNull();
    expect(within(nav).queryByRole('button', { name: 'Clips' })).toBeNull();
    // "Clips" survives as a type FILTER inside the library, which is where it went.
    expect(screen.getByRole('button', { name: 'Clips', pressed: false })).toBeTruthy();
  });

  it('opens the player as an overlay over the library and closes it with Escape', () => {
    renderApp();
    connect();

    const card = screen.getByRole('button', { name: 'Open Session 1' });
    // A real browser focuses a button on click; fireEvent does not, and the overlay's focus restore is
    // defined against whatever was focused when it opened — so focus the card the way a click would.
    card.focus();
    fireEvent.click(card);

    const overlay = screen.getByRole('dialog', { name: 'Player — Session 1' });
    expect(overlay).toBeTruthy();
    // The real PlayerView is inside the overlay, playing the item that was clicked.
    expect(within(overlay).getByTestId('player-title').textContent).toBe('Session 1');
    // Focus moved into the overlay rather than staying on a card the overlay is covering.
    expect(overlay.contains(document.activeElement)).toBe(true);

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog', { name: 'Player — Session 1' })).toBeNull();
    // Back on the library, with focus returned to the card that opened the player.
    expect(screen.getByTestId('library-grid')).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Open Session 1' }));
  });

  it('closes the overlay with its close affordance', () => {
    renderApp();
    connect();

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    expect(screen.getByRole('dialog', { name: 'Player — Nice shot' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Close player' }));
    expect(screen.queryByTestId('player-overlay')).toBeNull();
  });

  it('raises the overlay on the shell\'s single content source, without re-asking the backend', () => {
    renderApp();
    connect();
    const ws = activeSocket();
    const listContents = () => ws.sent.filter((frame) => frame.includes('"ListContent"')).length;
    const before = listContents();

    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    // The player is handed the shell's source, so it must not create a second IPC source of its own —
    // which would double-ask the backend for the whole content list every time a card is clicked.
    expect(screen.getByTestId('player-overlay')).toBeTruthy();
    expect(listContents()).toBe(before);
  });

  it('returns to the same filter and page state after the overlay closes', () => {
    renderApp();
    connect();

    // Narrow the library to clips and search for one, so there is real state to come back to.
    fireEvent.click(screen.getByRole('button', { name: 'Clips' }));
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'shot' } });
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    fireEvent.keyDown(document, { key: 'Escape' });

    // The library was covered, not unmounted: its query is untouched.
    expect(screen.getByRole('button', { name: 'Clips', pressed: true })).toBeTruthy();
    expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('shot');
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Open Nice shot' })).toBeTruthy();
  });

  it('shows Connected when the mock backend opens the socket', () => {
    renderApp();
    const ws = activeSocket();
    expect(ws).toBeDefined();
    act(() => {
      ws.serverOpen();
    });
    expect(screen.getByTestId('connection-state').textContent).toBe('Connected');
  });

  it('shows an error banner for an error push and dismisses it', () => {
    renderApp();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
      ws.serverMessage(
        JSON.stringify({
          method: 'error',
          content: { message: 'The bookmark could not be saved — check the recording folder is writable.' },
        }),
      );
    });
    expect(screen.getByRole('alert').textContent).toContain(
      'The bookmark could not be saved — check the recording folder is writable.',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss error' }));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('recorder bar shows a state push from the backend', () => {
    renderApp();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
      ws.serverMessage(
        JSON.stringify({
          method: 'state',
          content: {
            state: { recording: true, game: { id: 'cs2', name: 'Counter-Strike 2', detected: true } },
          },
        }),
      );
    });
    // Scoped to the recorder bar: the detected game is now legitimately on screen twice, since the
    // library shows the same game on a card and in its game filter. Asserting document-wide would
    // pass or fail on the library's content, which is not what this test is about.
    const bar = within(screen.getByRole('banner'));
    expect(bar.getByText('Recording')).toBeTruthy();
    expect(bar.getByText('Counter-Strike 2')).toBeTruthy();
  });
});
