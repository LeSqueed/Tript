// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status. Three
// routes, not five.

import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ErrorBanner } from '../components/ErrorBanner';
import { DisplayFallbackBanner } from '../components/DisplayFallbackBanner';
import { LibraryView } from '../components/LibraryView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { TrashView } from '../components/TrashView';
import { useTrash } from '../components/trash/useTrash';
import { PlayerOverlay } from '../components/library/PlayerOverlay';
import { itemLabel } from '../components/library/libraryModel';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import { hasSessionToken } from '../ipc/sessionToken';
import './app.css';

export type Route = 'library' | 'trash' | 'settings';

export function App({ ipcOptions }: { ipcOptions?: IpcClientOptions }) {
  // Without the launch token every listener refuses this page: the socket, the videos, the
  // thumbnails. Rendering the shell anyway would be an empty library over a socket reconnecting
  // forever, which reads as a broken backend. Say what is wrong and open nothing.
  if (!hasSessionToken()) {
    return <MissingKeyNotice />;
  }
  return <AppShell ipcOptions={ipcOptions} />;
}

function MissingKeyNotice() {
  return (
    <div className="missing-key" data-testid="missing-key-notice">
      <div className="panel">
        <h2>This window is missing its launch key</h2>
        <p className="muted">
          Tript issues a new key each time it starts and only answers requests that carry it. Open the
          address Tript printed when it started, or start Tript again to get a fresh one.
        </p>
      </div>
    </div>
  );
}

function AppShell({ ipcOptions }: { ipcOptions?: IpcClientOptions }) {
  const { client, connectionState } = useIpcClient(ipcOptions);
  const [route, setRoute] = useState<Route>('library');
  // The item the player overlay is showing, or null when the overlay is down.
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);

  // One IPC session source for the shell's lifetime, shared by the library and the player so a
  // `content` push anywhere is reflected everywhere.
  const source = useIpcSessionSource(client);
  const { items } = useSessionSource(source);

  // Owned by the shell, not by the trash screen: the library's delete confirmation quotes
  // `retentionHours`, and it must be right the first time a user deletes anything.
  const trash = useTrash(client);

  // The scrolling content column. The DOM keeps a mounted subtree's scroll offset by itself, but the
  // column is switched to `overflow: hidden` while the overlay is up (see app.css) so the library
  // cannot be scrolled behind it — hence the offset is captured on open and written back on close,
  // rather than being trusted to survive that switch in every browser.
  const contentRef = useRef<HTMLElement>(null);
  const savedScrollTop = useRef(0);
  const overlayWasOpen = useRef(false);

  const openInPlayer = useCallback((item: ContentItem) => {
    savedScrollTop.current = contentRef.current?.scrollTop ?? 0;
    setPlayerItem(item);
  }, []);

  const closePlayer = useCallback(() => setPlayerItem(null), []);

  useLayoutEffect(() => {
    if (playerItem !== null) {
      overlayWasOpen.current = true;
      return;
    }
    // Only on the open → closed transition; on first mount there is nothing to restore and writing 0
    // would be a scroll the user did not ask for.
    if (overlayWasOpen.current) {
      overlayWasOpen.current = false;
      if (contentRef.current) {
        contentRef.current.scrollTop = savedScrollTop.current;
      }
    }
  }, [playerItem]);

  const showLibrary = useCallback(() => setRoute('library'), []);
  const showTrash = useCallback(() => setRoute('trash'), []);
  const showSettings = useCallback(() => setRoute('settings'), []);

  const overlayOpen = playerItem !== null;

  return (
    <div className={overlayOpen ? 'app-shell player-open' : 'app-shell'}>
      <RecorderBar client={client} connectionState={connectionState} />
      <ErrorBanner client={client} />
      <DisplayFallbackBanner client={client} />
      <nav className="app-nav" aria-label="Primary">
        <button
          type="button"
          className={route === 'library' ? 'nav-item active' : 'nav-item'}
          onClick={showLibrary}
        >
          Library
        </button>
        <button
          type="button"
          className={route === 'trash' ? 'nav-item active' : 'nav-item'}
          onClick={showTrash}
        >
          Trash
          {trash.entries.length > 0 && (
            <span className="nav-badge" data-testid="nav-trash-count">
              {trash.entries.length}
            </span>
          )}
        </button>
        <button
          type="button"
          className={route === 'settings' ? 'nav-item active' : 'nav-item'}
          onClick={showSettings}
        >
          Settings
        </button>
      </nav>
      <main className="app-content" ref={contentRef}>
        {route === 'library' && (
          <LibraryView
            client={client}
            items={items}
            onOpen={openInPlayer}
            retentionHours={trash.retentionHours}
          />
        )}
        {route === 'trash' && <TrashView trash={trash} />}
        {route === 'settings' && <SettingsView client={client} />}
      </main>
      {playerItem && (
        // Rendered as a sibling of the content column, not inside it: the overlay is full-bleed over
        // the whole shell (nav and recorder bar included), so nothing behind it is clickable while it
        // is up. `PlayerView` goes in unchanged — the overlay is chrome, not a second player.
        <PlayerOverlay title={itemLabel(playerItem)} onClose={closePlayer}>
          <PlayerView client={client} source={source} item={playerItem} />
        </PlayerOverlay>
      )}
    </div>
  );
}
