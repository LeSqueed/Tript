// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status. Routes
// between Library, Player (session) and Settings. This is the foundation — the panels themselves
// are stubs that prove the shell and the IPC round-trip.

import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { LibraryView } from '../components/LibraryView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { useState } from 'react';
import type { IpcClientOptions } from '../ipc/websocketClient';
import './app.css';

export type Route = 'library' | 'player' | 'settings';

export function App({ ipcOptions }: { ipcOptions?: IpcClientOptions }) {
  const { client, connectionState } = useIpcClient(ipcOptions);
  const [route, setRoute] = useState<Route>('library');

  return (
    <div className="app-shell">
      <RecorderBar client={client} connectionState={connectionState} />
      <nav className="app-nav" aria-label="Primary">
        <button
          type="button"
          className={route === 'library' ? 'nav-item active' : 'nav-item'}
          onClick={() => setRoute('library')}
        >
          Library
        </button>
        <button
          type="button"
          className={route === 'player' ? 'nav-item active' : 'nav-item'}
          onClick={() => setRoute('player')}
        >
          Player
        </button>
        <button
          type="button"
          className={route === 'settings' ? 'nav-item active' : 'nav-item'}
          onClick={() => setRoute('settings')}
        >
          Settings
        </button>
      </nav>
      <main className="app-content">
        {route === 'library' && <LibraryView client={client} />}
        {route === 'player' && <PlayerView client={client} />}
        {route === 'settings' && <SettingsView client={client} />}
      </main>
    </div>
  );
}
