// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shell render test: the recorder bar, nav and theme render; navigation switches views; the
// connection state is shown even when the backend is not running (fails gracefully). The IPC client
// is mocked at the socket level so the real client logic runs against a test double.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, act, within } from '@testing-library/react';
import { App } from './App';
import { MockWebSocket } from '../ipc/test/mockWebSocket';
import { captureSessionToken } from '../ipc/sessionToken';

/**
 * Make this test's window narrow. The setup stub reports "not compact", so a player opened at
 * desktop width is the full-size route; the overlay is what narrow windows get. Call before render.
 */
function compactWindow(): void {
  window.matchMedia = ((query: string) => ({
    matches: true,
    media: query,
    onchange: null,
    addEventListener: () => {},
    removeEventListener: () => {},
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  })) as typeof window.matchMedia;
}

/** Stands in for the 256-bit token the host mints per launch. */
const TOKEN = 'f00dcafe1234567890';

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
    // The real page is served only to a request carrying the launch token, so the shell always
    // starts with one; a token-less start is its own describe block below.
    captureSessionToken(`?k=${TOKEN}`);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    captureSessionToken('');
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
    // These are the user-facing routes; Player and Clips were folded into the library.
    expect(within(nav).getByRole('button', { name: 'Library' })).toBeTruthy();
    expect(within(nav).getByRole('button', { name: 'Trash' })).toBeTruthy();
    expect(within(nav).getByRole('button', { name: 'Settings' })).toBeTruthy();
    expect(within(nav).queryByRole('button', { name: 'Player' })).toBeNull();
    expect(within(nav).queryByRole('radio', { name: 'Clips' })).toBeNull();
    // "Clips" survives as a type FILTER inside the library, which is where it went.
    expect(screen.getByRole('radio', { name: 'Clips', checked: false })).toBeTruthy();
  });

  it('opens the player as a route at desktop width, keeping the library mounted', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    // The player is on the page...
    expect(document.querySelector('.player-view')).not.toBeNull();
    // ...as a route, not as the overlay layer.
    expect(screen.queryByTestId('player-overlay')).toBeNull();
    // ...and the library is still mounted underneath, merely hidden, so its state survives.
    const library = document.querySelector('.library-view');
    expect(library).not.toBeNull();
    expect(library?.closest('[hidden]')).not.toBeNull();
    // The topbar names the item and offers the way back.
    expect(screen.getByRole('button', { name: '← Library' })).toBeTruthy();
  });

  it('returns from the player route to the library', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: '← Library' }));

    expect(document.querySelector('.player-view')).toBeNull();
    expect(document.querySelector('.library-view')?.closest('[hidden]')).toBeNull();
  });

  it('opens the player as an overlay over the library and closes it with Escape', () => {
    compactWindow();
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
    expect(within(overlay).getByTestId('player-overlay-title').textContent).toBe('Session 1');
    // Focus moved into the overlay rather than staying on a card the overlay is covering.
    expect(overlay.contains(document.activeElement)).toBe(true);

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog', { name: 'Player — Session 1' })).toBeNull();
    // Back on the library, with focus returned to the card that opened the player.
    expect(screen.getByTestId('library-grid')).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Open Session 1' }));
  });

  it('closes the overlay with its close affordance', () => {
    compactWindow();
    renderApp();
    connect();

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    expect(screen.getByRole('dialog', { name: 'Player — Nice shot' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Close player' }));
    expect(screen.queryByTestId('player-overlay')).toBeNull();
  });

  it('raises the overlay on the shell\'s single content source, without re-asking the backend', () => {
    compactWindow();
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
    compactWindow();
    renderApp();
    connect();

    // Narrow the library to clips and search for one, so there is real state to come back to.
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'shot' } });
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    fireEvent.keyDown(document, { key: 'Escape' });

    // The library was covered, not unmounted: its query is untouched.
    expect(screen.getByRole('radio', { name: 'Clips', checked: true })).toBeTruthy();
    expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('shot');
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Open Nice shot' })).toBeTruthy();
  });

  it('asks the backend for the trash and shows what comes back on its own route', () => {
    renderApp();
    connect();
    const ws = activeSocket();
    // The trash is not part of the NewConnection push, so it has to be asked for, like the content list.
    expect(ws.sent.some((frame) => frame.includes('"ListTrash"'))).toBe(true);

    act(() => {
      ws.serverMessage(
        JSON.stringify({
          method: 'trash',
          content: {
            retentionHours: 72,
            entries: [
              {
                id: 't1',
                contentType: 'recording',
                fileName: 'session-1.mp4',
                title: 'Session 1',
                deletedAt: 1787011200 - 3600,
                purgeAt: 1787011200 + 71 * 3600,
              },
            ],
          },
        }),
      );
    });

    // The count is visible from the nav, not only once you are already looking at the trash.
    expect(screen.getByTestId('nav-trash-count').textContent).toBe('1');

    const nav = screen.getByRole('navigation', { name: 'Primary' });
    fireEvent.click(within(nav).getByRole('button', { name: /Trash/ }));
    expect(screen.getByTestId('trash-list')).toBeTruthy();
    expect(screen.getByTestId('trash-retention').textContent).toContain('3 days');
  });

  it('quotes the pushed retention in the library\'s delete confirmation', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(
        JSON.stringify({ method: 'trash', content: { entries: [], retentionHours: 72 } }),
      );
    });

    fireEvent.click(screen.getByRole('button', { name: 'Delete Session 1' }));
    // The shell owns the trash state precisely so the FIRST delete can tell the truth about it.
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('for the next 3 days');
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

// The host refuses every listener without the token, so a token-less page can do nothing at all.
// Silently reconnecting behind an empty library would look like a broken backend; say what is wrong
// instead. In practice this is what a bare `vite dev` (on :2883) hits.
describe('App without a session token', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
    captureSessionToken('');
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  function renderApp() {
    return render(<App ipcOptions={{ createSocket: (url: string) => new ContentBackend(url) }} />);
  }

  it('explains itself instead of rendering the shell', () => {
    renderApp();

    expect(screen.queryByRole('navigation', { name: 'Primary' })).toBeNull();
    expect(screen.queryByTestId('connection-state')).toBeNull();
    expect(screen.getByTestId('missing-key-notice')).toBeTruthy();
  });

  it('opens no socket, rather than reconnecting forever against a listener that refuses it', () => {
    renderApp();
    vi.advanceTimersByTime(60_000);

    expect(MockWebSocket.instances).toHaveLength(0);
  });
});

describe('the session token in the rendered page', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
    captureSessionToken(`?k=${TOKEN}`);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    captureSessionToken('');
  });

  // It necessarily rides on the media URLs (a <video> has no other way to authenticate), but it must
  // never be shown to the user or written to the console, where it outlives the page in a log.
  it('is never rendered as text and never logged', () => {
    const methods = ['log', 'info', 'warn', 'error', 'debug'] as const;
    const spies = methods.map((method) => vi.spyOn(console, method).mockImplementation(() => {}));

    render(<App ipcOptions={{ createSocket: (url: string) => new ContentBackend(url) }} />);
    act(() => {
      MockWebSocket.instances[MockWebSocket.instances.length - 1].serverOpen();
    });

    expect(document.body.textContent ?? '').not.toContain(TOKEN);
    for (const spy of spies) {
      for (const call of spy.mock.calls) {
        expect(call.map(String).join(' ')).not.toContain(TOKEN);
      }
      spy.mockRestore();
    }
  });
});

// A tab left open across a host restart holds the previous launch key, so the socket, the videos and
// the thumbnails all 403. That is correct, but on its own it presents as a shell that never fills in
// — the user has no way to know a reload of the printed address is what they need.
describe('App against a host that no longer accepts this page\'s key', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
    captureSessionToken(`?k=${TOKEN}`);
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
    vi.unstubAllGlobals();
    captureSessionToken('');
  });

  /** Answers the UI-host probe; the socket is left to never open, as a refused handshake leaves it. */
  function hostAnswers(status: number): void {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status })));
  }

  async function settle(ms: number): Promise<void> {
    await act(async () => {
      vi.advanceTimersByTime(ms);
    });
  }

  it('says the key is stale once the host answers 403', async () => {
    hostAnswers(403);
    render(<App ipcOptions={{ createSocket: (url: string) => new ContentBackend(url) }} />);

    // Nothing while the socket is merely retrying.
    await settle(1_000);
    expect(screen.queryByTestId('connection-banner')).toBeNull();

    await settle(5_000);
    const banner = screen.getByTestId('connection-banner');
    expect(banner.textContent).toMatch(/restarted/i);
  });

  it('does not blame the key when the host still accepts it', async () => {
    hostAnswers(200);
    render(<App ipcOptions={{ createSocket: (url: string) => new ContentBackend(url) }} />);

    await settle(6_000);
    expect(screen.getByTestId('connection-banner').textContent).not.toMatch(/key/i);
  });

  it('takes the banner back down when the socket comes up', async () => {
    hostAnswers(403);
    render(<App ipcOptions={{ createSocket: (url: string) => new ContentBackend(url) }} />);

    await settle(6_000);
    expect(screen.getByTestId('connection-banner')).toBeTruthy();

    act(() => {
      MockWebSocket.instances[MockWebSocket.instances.length - 1].serverOpen();
    });
    await settle(60_000);
    expect(screen.queryByTestId('connection-banner')).toBeNull();
  });
});
