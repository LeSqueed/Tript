// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status.
//
// TWO ROUTES, NOT FOUR. The nav is `[ Library ] [ Settings ]`. `clips` is gone because the library is
// one grid over sessions AND clips with a type filter — the clips page was the same list, card, sort
// and pagination a second time. `player` is gone because the player is now an OVERLAY over the
// library rather than a destination: clicking a card raises it, closing it lowers it, and the library
// underneath was never unmounted, so the user's filters, sort, page and scroll position are still
// exactly as they left them. The library is the single home.
//
// The overlay's one piece of state is the item it plays. Everything else "restored" on close is
// restored by not having been destroyed: the library component stays mounted the whole time. The only
// exception is the content column's scroll offset — the column is hidden from scrolling while the
// overlay is up (so the page behind cannot be scrolled away under it), and the offset is captured on
// open and put back on close.
//
// The error banner sits at the shell, directly under the recorder bar, because a failure the backend
// reports (a bookmark that could not be persisted, say) belongs to the app rather than to whichever
// view happens to be open — the user must see it even after navigating away from where it happened.
//
// The IPC session source is owned here, at the shell, and shared by the library and the player. The
// backend broadcasts content on every change, and this is a single app-level subscription, so one
// `ListContent` is sent per connection and every view reflects the same live list — one source, one
// ask, however many readers.

import { useCallback, useLayoutEffect, useRef, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ErrorBanner } from '../components/ErrorBanner';
import { LibraryView } from '../components/LibraryView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { PlayerOverlay } from '../components/library/PlayerOverlay';
import { itemLabel } from '../components/library/libraryModel';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import './app.css';

export type Route = 'library' | 'settings';

export function App({ ipcOptions }: { ipcOptions?: IpcClientOptions }) {
  const { client, connectionState } = useIpcClient(ipcOptions);
  const [route, setRoute] = useState<Route>('library');
  // The item the player overlay is showing, or null when the overlay is down.
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);

  // One IPC session source for the shell's lifetime, shared by the library and the player so a
  // `content` push anywhere is reflected everywhere.
  const source = useIpcSessionSource(client);
  const { items } = useSessionSource(source);

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
  const showSettings = useCallback(() => setRoute('settings'), []);

  const overlayOpen = playerItem !== null;

  return (
    <div className={overlayOpen ? 'app-shell player-open' : 'app-shell'}>
      <RecorderBar client={client} connectionState={connectionState} />
      <ErrorBanner client={client} />
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
          className={route === 'settings' ? 'nav-item active' : 'nav-item'}
          onClick={showSettings}
        >
          Settings
        </button>
      </nav>
      <main className="app-content" ref={contentRef}>
        {route === 'library' && <LibraryView client={client} items={items} onOpen={openInPlayer} />}
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
