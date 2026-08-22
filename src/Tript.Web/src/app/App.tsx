// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status. Three
// routes, not five.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ErrorBanner } from '../components/ErrorBanner';
import { WarningBanner } from '../components/WarningBanner';
import { ConnectionBanner } from '../components/ConnectionBanner';
import { DisplayFallbackBanner } from '../components/DisplayFallbackBanner';
import { LibraryView } from '../components/LibraryView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { useTrash } from '../components/trash/useTrash';
import { itemLabel } from '../components/library/libraryModel';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import { hasSessionToken } from '../ipc/sessionToken';
import { useHostReachability } from './useHostReachability';
import { TripwireMark } from '../components/TripwireMark';
import { Icon } from '../components/ui/Icon';
import { trainingEnabled } from '../buildFeatures';
import { TrainingView } from '../components/TrainingView';
import './app.css';

export type Route = 'library' | 'settings' | 'player' | 'training';

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
        <TripwireMark size={48} label="Tript" />
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
  // A key is present (App checked) but may no longer be accepted: the host mints a new one per
  // launch, so a tab left open across a restart 403s on everything and would otherwise just sit
  // there empty.
  const reachability = useHostReachability(connectionState);
  const [route, setRoute] = useState<Route>('library');
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);
  const [playerTitle, setPlayerTitle] = useState('');
  const [playerNavigation, setPlayerNavigation] = useState<ContentItem[]>([]);

  // One IPC session source for the shell's lifetime, shared by the library and the player so a
  // `content` push anywhere is reflected everywhere.
  const source = useIpcSessionSource(client);
  const { items, loaded } = useSessionSource(source);

  // Owned by the shell, not by the trash screen: the library's delete confirmation quotes
  // `retentionHours`, and it must be right the first time a user deletes anything.
  const trash = useTrash(client);

  // The scrolling content column. Save its position while the library remains mounted under the
  // player route so returning to Library restores the user's place.
  const contentRef = useRef<HTMLDivElement>(null);
  const savedScrollTop = useRef(0);
  const overlayWasOpen = useRef(false);

  const openInPlayer = useCallback(
    (item: ContentItem, resultItems: ContentItem[]) => {
      savedScrollTop.current = contentRef.current?.scrollTop ?? 0;
      setPlayerItem(item);
      setPlayerTitle(itemLabel(item));
      setPlayerNavigation(resultItems);
      setRoute('player');
    },
    [],
  );

  const closePlayer = useCallback(() => {
    setPlayerItem(null);
    setPlayerTitle('');
    setPlayerNavigation([]);
    setRoute((current) => (current === 'player' ? 'library' : current));
  }, []);

  useEffect(() => {
    if (route !== 'player') {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        if (event.target instanceof Element && event.target.closest('[role="dialog"]')) return;
        event.preventDefault();
        closePlayer();
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [route, closePlayer]);

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

  useEffect(() => {
    if (!playerItem) {
      return;
    }
    if (!items.some((candidate) => candidate.filePath === playerItem.filePath)) {
      closePlayer();
      return;
    }
    setPlayerNavigation((previous) =>
      previous.filter((candidate) => items.some((current) => current.filePath === candidate.filePath)),
    );
  }, [items, playerItem, closePlayer]);

  // Leaving for another destination closes whatever was open in the player route.
  const leaveFor = useCallback((next: Route) => {
    setRoute(next);
    setPlayerItem(null);
  }, []);
  const showLibrary = useCallback(() => leaveFor('library'), [leaveFor]);
  const showSettings = useCallback(() => leaveFor('settings'), [leaveFor]);
  const showTraining = useCallback(() => leaveFor('training'), [leaveFor]);

  return (
    <div className="app-shell">
      <header className="app-topbar">
        <div className="app-brand">
          <TripwireMark size={26} />
          <strong>Tript</strong>
        </div>
        {/* The player is a workspace inside Tript, so primary navigation stays available while reviewing. */}
        <nav className="app-nav" aria-label="Primary">
          <button
            type="button"
            className={route === 'library' || route === 'player' ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'library' || route === 'player' ? 'page' : undefined}
            onClick={showLibrary}
          >
            <Icon name="library" className="nav-glyph" />
            <span>Library</span>
          </button>
          <button
            type="button"
            className={route === 'settings' ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'settings' ? 'page' : undefined}
            onClick={showSettings}
          >
            <Icon name="settings" className="nav-glyph" />
            <span>Settings</span>
          </button>
          {trainingEnabled && (
            <button
              type="button"
              className={route === 'training' ? 'nav-item active' : 'nav-item'}
              aria-current={route === 'training' ? 'page' : undefined}
              onClick={showTraining}
            >
              <Icon name="clip" className="nav-glyph" />
              <span>Training</span>
            </button>
          )}
        </nav>
        {route === 'player' && <span className="app-topbar-context">{playerTitle}</span>}
        <RecorderBar client={client} connectionState={connectionState} />
      </header>
      <main className="app-main">
        <ConnectionBanner reachability={reachability} />
        <ErrorBanner client={client} />
        <WarningBanner client={client} />
        <DisplayFallbackBanner client={client} />
        <div
          className={route === 'player' ? 'app-content app-content-player' : 'app-content'}
          ref={contentRef}
        >
          {(route === 'library' || route === 'player') && (
            // Mounted but hidden while the player route is up. Unmounting would lose the user's
            // filters, sort, page and scroll while the player is open.
            <div hidden={route === 'player'}>
              <LibraryView
                client={client}
                items={items}
                connectionState={connectionState}
                contentLoaded={loaded}
                onOpen={openInPlayer}
                retentionHours={trash.retentionHours}
                trash={trash}
              />
            </div>
          )}
          {route === 'player' && playerItem && (
            <PlayerView
              client={client}
              trainingEnabled={trainingEnabled}
              source={source}
              item={playerItem}
              navigationItems={playerNavigation}
              onBack={showLibrary}
              onItemChange={(item) => setPlayerTitle(itemLabel(item))}
            />
          )}
          {route === 'settings' && <SettingsView client={client} />}
          {route === 'training' && trainingEnabled && <TrainingView client={client} />}
        </div>
      </main>
    </div>
  );
}
