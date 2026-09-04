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

const HIGHLIGHT_1 = {
  contentType: 'clip',
  fileName: 'highlight-1.mp4',
  filePath: 'clips/highlight-1.mp4',
  title: 'First highlight',
  automated: true,
  sourceSessionPath: SESSION_1.filePath,
  clipStartTime: 10,
};

const HIGHLIGHT_2 = {
  ...HIGHLIGHT_1,
  fileName: 'highlight-2.mp4',
  filePath: 'clips/highlight-2.mp4',
  title: 'Second highlight',
  clipStartTime: 20,
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
    window.location.hash = '';
    captureSessionToken('');
  });

  function renderApp(trainingFeatureEnabled?: boolean) {
    const factory = (url: string) => new ContentBackend(url);
    const result = render(
      <App ipcOptions={{ createSocket: factory }} trainingFeatureEnabled={trainingFeatureEnabled} />,
    );
    return { ...result, factory };
  }

  it('says only that it is not connected before the backend answers', () => {
    renderApp();
    // Nothing else on the bar is actionable yet, so nothing else is offered.
    expect(screen.getByText(/not connected/i)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Record' })).toBeNull();
    expect(screen.queryByText('Stopped')).toBeNull();
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
    expect(screen.getByTestId('library-groups')).toBeTruthy();
  });

  it('requests games once and does not request them again in response to gameList', () => {
    renderApp();
    connect();
    const socket = activeSocket();
    const listGamesCount = () => socket.sent
      .map((frame) => JSON.parse(frame) as { method?: string })
      .filter((frame) => frame.method === 'ListGames').length;

    expect(listGamesCount()).toBe(1);
    act(() => socket.serverMessage(JSON.stringify({
      method: 'gameList',
      content: [{ id: 'Overwatch', name: 'Overwatch', detected: false, builtIn: true }],
    })));
    expect(listGamesCount()).toBe(1);
  });

  it('honours a settings startup fragment without changing normal navigation', () => {
    window.location.hash = '#settings';
    renderApp();
    connect();

    expect(screen.getByRole('tab', { name: 'General' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Recording' })).toBeTruthy();
    expect(screen.queryByTestId('library-groups')).toBeNull();
  });

  it('follows a settings fragment changed by the desktop shell', () => {
    renderApp();
    connect();

    act(() => {
      window.location.hash = '#settings';
      window.dispatchEvent(new HashChangeEvent('hashchange'));
    });

    expect(screen.getByRole('tab', { name: 'General' })).toBeTruthy();
    expect(screen.queryByTestId('library-groups')).toBeNull();
  });

  it('follows a settings command from the desktop shell without reloading', () => {
    renderApp();
    connect();

    act(() => {
      window.dispatchEvent(new CustomEvent('tript:navigate', { detail: 'settings' }));
    });

    expect(screen.getByRole('tab', { name: 'General' })).toBeTruthy();
    expect(screen.queryByTestId('library-groups')).toBeNull();
  });

  it('can reopen settings after navigating back to the library', () => {
    renderApp();
    connect();

    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    expect(window.location.hash).toBe('#settings');
    fireEvent.click(screen.getByRole('button', { name: 'Library' }));
    expect(window.location.hash).toBe('#library');

    act(() => {
      window.dispatchEvent(new CustomEvent('tript:navigate', { detail: 'settings' }));
    });

    expect(screen.getByRole('tab', { name: 'General' })).toBeTruthy();
  });

  it('puts navigation in the topbar and does not repeat the route name below it', () => {
    renderApp();
    connect();
    // No rail: the nav sits in the topbar beside the brand.
    expect(document.querySelector('.app-rail')).toBeNull();
    const topbar = document.querySelector('.app-topbar') as HTMLElement;
    expect(topbar).not.toBeNull();
    expect(within(topbar).getByRole('navigation', { name: 'Primary' })).toBeTruthy();
    // The active nav item names the screen, so no heading says it a second time.
    expect(screen.queryByRole('heading', { level: 1, name: 'Library' })).toBeNull();
  });

  it('offers two destinations, with Clips and Trash as library filters', () => {
    renderApp();
    connect();
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    // Player, Clips and Trash all folded into the library, leaving two places to be.
    expect(within(nav).getByRole('button', { name: 'Library' })).toBeTruthy();
    expect(within(nav).getByRole('button', { name: 'Settings' })).toBeTruthy();
    expect(within(nav).queryByRole('button', { name: 'Player' })).toBeNull();
    expect(within(nav).queryByRole('button', { name: /Trash/ })).toBeNull();
    // Both survive as type FILTERS inside the library, which is where they went.
    expect(screen.getByRole('radio', { name: 'Clips', checked: false })).toBeTruthy();
    expect(screen.getByRole('radio', { name: 'Trash', checked: false })).toBeTruthy();
  });

  it('opens the player as a route at desktop width, keeping the library mounted', () => {
    renderApp();
    connect();
    const library = document.querySelector('.library-view') as HTMLElement;
    expect(library.querySelectorAll('img').length).toBeGreaterThan(0);
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    // The player is on the page...
    expect(document.querySelector('.player-view')).not.toBeNull();
    // ...as a route, not as the overlay layer.
    expect(screen.queryByTestId('player-overlay')).toBeNull();
    // ...and the library is still mounted underneath, merely hidden, so its state survives.
    expect(library).not.toBeNull();
    expect(library?.closest('[hidden]')).not.toBeNull();
    expect(library.querySelectorAll('img')).toHaveLength(0);
    // The player remains inside the shell: primary navigation stays visible and Library is active.
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).getByRole('button', { name: 'Library' }).getAttribute('aria-current')).toBe('page');
    expect(document.querySelector('.app-topbar-context')?.textContent).toBe('Session 1');
  });

  it('opens a missing-video recording directly in its exact automatic highlights', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: {
          content: [
            HIGHLIGHT_2,
            { ...HIGHLIGHT_1, automated: false, fileName: 'manual.mp4', filePath: 'clips/manual.mp4' },
            { ...HIGHLIGHT_1, sourceSessionPath: SESSION_2.filePath, fileName: 'other.mp4', filePath: 'clips/other.mp4' },
            HIGHLIGHT_1,
            { ...SESSION_1, videoMissing: true },
          ],
        },
      }));
    });

    const library = document.querySelector('.library-view') as HTMLElement;
    const missingCard = screen.getByRole('button', { name: 'Open Session 1' });
    expect(within(missingCard).getAllByRole('presentation')).toHaveLength(2);
    fireEvent.click(missingCard);

    expect(screen.getByRole('heading', { name: 'Session 1' })).toBeTruthy();
    expect(screen.getByText('2 highlights')).toBeTruthy();
    expect(screen.getAllByRole('button', { name: /Open (First|Second) highlight/ }).map((button) => button.getAttribute('aria-label'))).toEqual([
      'Open First highlight',
      'Open Second highlight',
    ]);
    expect(screen.queryByRole('button', { name: 'Open manual.mp4' })).toBeNull();
    expect(document.querySelector('.player-view')).toBeNull();
    expect(library.closest('[hidden]')).not.toBeNull();
    expect(library.querySelectorAll('img')).toHaveLength(0);

    fireEvent.click(screen.getByRole('button', { name: 'Back to library' }));
    expect(library.closest('[hidden]')).toBeNull();
    expect(within(screen.getByRole('button', { name: 'Open Session 1' })).getAllByRole('presentation')).toHaveLength(2);
  });

  it('refreshes the open player item when content metadata changes', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    expect(screen.getByRole('button', { name: 'Add to favorites' })).toBeTruthy();

    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [CLIP_1, SESSION_2, { ...SESSION_1, favorite: true }] },
      }));
    });

    expect(screen.getByRole('button', { name: 'Remove from favorites' })).toBeTruthy();
  });

  it('keeps the navigated item current across later content pushes', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Next item' }));
    const selected = document.querySelector('video')?.getAttribute('aria-label');

    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [CLIP_1, { ...SESSION_2, favorite: true }, SESSION_1] },
      }));
    });

    expect(document.querySelector('video')?.getAttribute('aria-label')).toBe(selected);
  });

  it('confirms trash from a normal player entry', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));
    const dialog = screen.getByTestId('confirm-delete');
    expect(within(dialog).getByText('Delete Session 1?')).toBeTruthy();
    expect(activeSocket().sent.map((frame) => JSON.parse(frame)).some((frame) => frame.method === 'DeleteContent')).toBe(false);

    fireEvent.click(within(dialog).getByTestId('confirm-delete-confirm'));
    expect(activeSocket().sent.map((frame) => JSON.parse(frame))).toContainEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'recording', fileName: SESSION_1.filePath },
    });
  });

  it('uses the current settings default for player recording deletion', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'settings',
        content: { settings: { recording: { deleteLinkedHighlightsByDefault: true } } },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));

    const linked = screen.getByRole('checkbox', {
      name: 'Delete linked highlights (favourited highlights are kept)',
    });
    expect((linked as HTMLInputElement).checked).toBe(true);
    fireEvent.click(linked);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(activeSocket().sent.map((frame) => JSON.parse(frame))).toContainEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'recording', fileName: SESSION_1.filePath },
    });
  });

  it('sends the confirmed linked-highlight choice from a player recording deletion', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));
    fireEvent.click(screen.getByRole('checkbox', { name: /delete linked highlights/i }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(activeSocket().sent.map((frame) => JSON.parse(frame))).toContainEqual({
      method: 'DeleteContent',
      parameters: {
        contentType: 'recording',
        fileName: SESSION_1.filePath,
        deleteLinkedHighlights: true,
      },
    });
  });

  it('trashes immediately and advances while reviewing session highlights', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'View highlights (2)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open First highlight' }));

    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));

    expect(screen.queryByTestId('confirm-delete')).toBeNull();
    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('Second highlight');
    expect(activeSocket().sent.map((frame) => JSON.parse(frame))).toContainEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'clip', fileName: HIGHLIGHT_1.filePath },
    });
  });

  it('offers a restore on the highlight delete toast that puts the item back and opens it', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'View highlights (2)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open First highlight' }));
    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));

    const toast = screen.getByRole('status');
    expect(toast.textContent).toContain('Moved "First highlight" to trash.');
    expect(screen.queryByTestId('confirm-delete')).toBeNull();

    // The backend's trash push names the entry; the content push drops the item from the list.
    act(() => {
      const ws = activeSocket();
      ws.serverMessage(JSON.stringify({
        method: 'trash',
        content: {
          // The wire spells the entry with the BARE file name, not the relative path the content
          // list uses — the restore match depends on that.
          entries: [{ id: 'trash-1', contentType: 'clip', fileName: HIGHLIGHT_1.fileName, deletedAt: 1000, purgeAt: 2000 }],
          retentionHours: 72,
        },
      }));
      ws.serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, SESSION_1] },
      }));
    });

    fireEvent.click(screen.getByRole('button', { name: 'Restore' }));
    expect(activeSocket().sent.map((frame) => JSON.parse(frame))).toContainEqual({
      method: 'RestoreTrash',
      parameters: { entryIds: ['trash-1'] },
    });

    // The restore is only visible once the item is back in the content; that is also when the
    // player lands on it and the toast leaves.
    act(() => {
      const ws = activeSocket();
      ws.serverMessage(JSON.stringify({
        method: 'trash',
        content: { entries: [], retentionHours: 72 },
      }));
      ws.serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });

    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('First highlight');
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(screen.queryByRole('status')).toBeNull();

    // The restore lands back in the highlights flow: back from the player returns to the
    // session's clip list, not the library.
    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByText('2 highlights')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Back to session' })).toBeTruthy();
  });

  it('toasts a created clip with a View that opens the new clip in the player', () => {
    renderApp();
    connect();
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    // The player only marks against a length the media itself has reported.
    const video = document.querySelector('video') as HTMLVideoElement;
    act(() => {
      Object.defineProperty(video, 'duration', { configurable: true, writable: true, value: 120 });
      fireEvent.durationChange(video);
    });
    fireEvent.click(screen.getByRole('button', { name: 'Make a 10-second clip around where you are' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open clip dialog' }));
    fireEvent.change(screen.getByLabelText('Output'), { target: { value: 'Nice save' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create clip' }));

    const sent = activeSocket().sent
      .map((frame) => JSON.parse(frame) as { method?: string; parameters?: { id?: string } })
      .find((frame) => frame.method === 'CreateClip');
    expect(sent?.parameters?.id).toBeTruthy();

    const clip = {
      contentType: 'clip',
      fileName: 'nice-save.mp4',
      filePath: 'clips/nice-save.mp4',
      title: 'Nice save',
    };
    // The backend names the finished clip before the content list picks it up.
    act(() => {
      const ws = activeSocket();
      ws.serverMessage(JSON.stringify({
        method: 'importProgress',
        content: { id: sent?.parameters?.id, status: 'done', content: clip },
      }));
      ws.serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [CLIP_1, SESSION_2, SESSION_1, clip] },
      }));
    });

    expect(screen.getByRole('status').textContent).toContain('Created "Nice save".');
    fireEvent.click(screen.getByRole('button', { name: 'View' }));
    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('Nice save');
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('returns through session review to the source recording after opening a highlight', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'View highlights (2)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open First highlight' }));
    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('First highlight');

    fireEvent.click(screen.getByRole('button', { name: 'Back' }));
    expect(screen.getByRole('button', { name: 'Back to session' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Back to session' }));

    expect(document.querySelector('.player-view')).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Back to session' })).toBeNull();
    expect(screen.queryByText('2 highlights')).toBeNull();
    expect(document.querySelector('.app-topbar-context')?.textContent).toBe('Session 1');
    expect(document.querySelector('video')?.getAttribute('aria-label')).toContain('Session 1');
  });

  it('returns to the library when the source recording vanishes while viewing a highlight', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'View highlights (2)' }));
    fireEvent.click(screen.getByRole('button', { name: 'Open First highlight' }));

    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1] },
      }));
    });

    expect(document.querySelector('.player-view')).toBeNull();
    expect(document.querySelector('.library-view')?.closest('[hidden]')).toBeNull();
  });

  it('returns from the player route to the library', () => {
    renderApp();
    connect();
    const library = document.querySelector('.library-view') as HTMLElement;
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    expect(library.querySelectorAll('img')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Library' }));

    expect(document.querySelector('.player-view')).toBeNull();
    expect(document.querySelector('.library-view')?.closest('[hidden]')).toBeNull();
    expect(library.querySelectorAll('img').length).toBeGreaterThan(0);
  });

  it('returns to the library when an open session vanishes from a content push', () => {
    renderApp();
    connect();
    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [HIGHLIGHT_2, HIGHLIGHT_1, SESSION_1] },
      }));
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));
    fireEvent.click(screen.getByRole('button', { name: 'View highlights (2)' }));

    act(() => {
      activeSocket().serverMessage(JSON.stringify({
        method: 'content',
        content: { content: [] },
      }));
    });

    expect(document.querySelector('.player-view')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Back to session' })).toBeNull();
    expect(document.querySelector('.library-view')?.closest('[hidden]')).toBeNull();
  });

  it('keeps the player inside the shell at compact width', () => {
    compactWindow();
    renderApp();
    connect();

    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    expect(screen.queryByTestId('player-overlay')).toBeNull();
    expect(document.querySelector('.player-view')).not.toBeNull();
    expect(screen.getByRole('navigation', { name: 'Primary' })).toBeTruthy();
  });

  it('returns from the compact player through primary navigation', () => {
    compactWindow();
    renderApp();
    connect();

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    expect(document.querySelector('.player-view')).not.toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Library' }));
    expect(document.querySelector('.player-view')).toBeNull();
  });

  it('uses the shell\'s single content source, without re-asking the backend', () => {
    compactWindow();
    renderApp();
    connect();
    const ws = activeSocket();
    const listContents = () => ws.sent.filter((frame) => frame.includes('"ListContent"')).length;
    const before = listContents();

    fireEvent.click(screen.getByRole('button', { name: 'Open Session 1' }));

    // The player is handed the shell's source, so it must not create a second IPC source of its own —
    // which would double-ask the backend for the whole content list every time a card is clicked.
    expect(document.querySelector('.player-view')).not.toBeNull();
    expect(listContents()).toBe(before);
  });

  it('returns to the same filter and page state after the player closes', () => {
    compactWindow();
    renderApp();
    connect();

    // Narrow the library to clips and search for one, so there is real state to come back to.
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'shot' } });
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);

    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    fireEvent.click(screen.getByRole('button', { name: 'Library' }));

    // The library was covered, not unmounted: its query is untouched.
    expect(screen.getByRole('radio', { name: 'Clips', checked: true })).toBeTruthy();
    expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('shot');
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Open Nice shot' })).toBeTruthy();
  });

  it('asks the backend for the trash and shows what comes back behind its filter', () => {
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

    // The count rides on the filter, so a full trash is visible without going there first.
    const trashFilter = screen.getByRole('radio', { name: 'Trash (1)' });
    fireEvent.click(trashFilter);
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

  it('shows no connection badge once connected, only what can be done', () => {
    renderApp();
    const ws = activeSocket();
    expect(ws).toBeDefined();
    act(() => {
      ws.serverOpen();
    });
    // A "Connected" badge is only information when it is false, so it is not rendered when true.
    expect(screen.queryByTestId('connection-state')).toBeNull();
    expect(screen.queryByText(/not connected/i)).toBeNull();
    expect(screen.getByRole('button', { name: 'Record' })).toBeTruthy();
  });

  it('wires the enabled training feature into navigation, its route, and the player', () => {
    renderApp(true);
    connect();
    const nav = screen.getByRole('navigation', { name: 'Primary' });

    fireEvent.click(screen.getByRole('button', { name: 'Open Session 2' }));
    expect(screen.getByRole('button', { name: 'Label frame' })).toBeTruthy();

    fireEvent.click(within(nav).getByRole('button', { name: 'Training' }));
    expect(screen.getByRole('heading', { name: 'Build a game-specific training set' })).toBeTruthy();
  });

  it('omits the training route and player feature when the build feature is disabled', () => {
    renderApp(false);
    connect();
    const nav = screen.getByRole('navigation', { name: 'Primary' });
    expect(within(nav).queryByRole('button', { name: 'Training' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Open Session 2' }));
    expect(screen.queryByRole('button', { name: 'Label frame' })).toBeNull();
  });

  it('shows an error toast for an error push and dismisses it', () => {
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

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => {
      vi.advanceTimersByTime(200); // the exit plays before the toast leaves
    });
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
    const bar = within(screen.getByTestId('recorder-bar'));
    // Elapsed time is the headline, not the word "Recording".
    expect(bar.getByTestId('recording-elapsed')).toBeTruthy();
    expect(bar.getByText('Counter-Strike 2')).toBeTruthy();
    expect(bar.getByRole('button', { name: 'Stop' })).toBeTruthy();
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
