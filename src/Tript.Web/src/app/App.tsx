// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status. Routes
// between Library, Clips, Player (session) and Settings. The library and clips list the backend's
// content via the IPC `content` push; clicking an item opens the player on it.
//
// The error banner sits at the shell, directly under the recorder bar, because a failure the backend
// reports (a bookmark that could not be persisted, say) belongs to the app rather than to whichever
// view happens to be open — the user must see it even after navigating away from where it happened.
//
// The IPC session source is owned here, at the shell, and shared by all three content views. The
// backend broadcasts content on every change, and this is a single app-level subscription, so one
// `ListContent` is sent per connection and every view reflects the same live list.

import { useCallback, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ErrorBanner } from '../components/ErrorBanner';
import { LibraryView } from '../components/LibraryView';
import { ClipsView } from '../components/ClipsView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import './app.css';

export type Route = 'library' | 'player' | 'settings' | 'clips';

export function App({ ipcOptions }: { ipcOptions?: IpcClientOptions }) {
  const { client, connectionState } = useIpcClient(ipcOptions);
  const [route, setRoute] = useState<Route>('library');
  // The item the player should open when a library/clips row is clicked.
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);

  // One IPC session source for the shell's lifetime, shared by the library, clips and player so a
  // `content` push anywhere is reflected everywhere.
  const source = useIpcSessionSource(client);
  const { sessions, clips } = useSessionSource(source);

  const openInPlayer = useCallback((item: ContentItem) => {
    setPlayerItem(item);
    setRoute('player');
  }, []);

  const showLibrary = useCallback(() => setRoute('library'), []);
  const showClips = useCallback(() => setRoute('clips'), []);
  const showPlayer = useCallback(() => {
    // A bare "Player" nav click opens the player without a preselected item — the session source's
    // first session plays (or "No sessions." when there is none).
    setPlayerItem(null);
    setRoute('player');
  }, []);
  const showSettings = useCallback(() => setRoute('settings'), []);

  return (
    <div className="app-shell">
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
          className={route === 'clips' ? 'nav-item active' : 'nav-item'}
          onClick={showClips}
        >
          Clips
        </button>
        <button
          type="button"
          className={route === 'player' ? 'nav-item active' : 'nav-item'}
          onClick={showPlayer}
        >
          Player
        </button>
        <button
          type="button"
          className={route === 'settings' ? 'nav-item active' : 'nav-item'}
          onClick={showSettings}
        >
          Settings
        </button>
      </nav>
      <main className="app-content">
        {route === 'library' && (
          <LibraryView client={client} sessions={sessions} onOpen={openInPlayer} />
        )}
        {route === 'clips' && <ClipsView clips={clips} onOpen={openInPlayer} />}
        {route === 'player' && <PlayerView client={client} source={source} item={playerItem ?? undefined} />}
        {route === 'settings' && <SettingsView client={client} />}
      </main>
    </div>
  );
}
