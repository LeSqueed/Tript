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
import { SessionClipsView } from '../components/SessionClipsView';
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

export type Route = 'library' | 'session' | 'settings' | 'player' | 'training';

type PhotinoShellWindow = Window & {
  external?: {
    sendMessage?: (message: string) => void;
  };
};

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
  const startupRoute = readStartupRoute();
  const [route, setRoute] = useState<Route>(startupRoute);
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);
  const [playerTitle, setPlayerTitle] = useState('');
  const [playerNavigation, setPlayerNavigation] = useState<ContentItem[]>([]);
  const [playerReturnRoute, setPlayerReturnRoute] = useState<'library' | 'session'>('library');
  const [sessionReview, setSessionReview] = useState<{ recording: ContentItem; clips: ContentItem[] } | null>(null);
  const [convertHdrClipsToSdr, setConvertHdrClipsToSdr] = useState(false);

  useEffect(() => {
    const remove = client.on('settings', (content) => {
      const settings = (content as { settings?: { general?: { convertHdrClipsToSdr?: boolean } } }).settings;
      setConvertHdrClipsToSdr(settings?.general?.convertHdrClipsToSdr === true);
    });
    client.send('ListSettings');
    return remove;
  }, [client]);

  useEffect(() => {
    const onHashChange = () => {
      setRoute(readStartupRoute());
      setPlayerItem(null);
      setPlayerTitle('');
      setPlayerNavigation([]);
    };
    const onNativeNavigation = (event: Event) => {
      if ((event as CustomEvent<string>).detail !== 'settings') {
        return;
      }
      replaceRouteHash('settings');
      setRoute('settings');
      setPlayerItem(null);
      setPlayerTitle('');
      setPlayerNavigation([]);
    };
    window.addEventListener('hashchange', onHashChange);
    window.addEventListener('tript:navigate', onNativeNavigation);
    return () => {
      window.removeEventListener('hashchange', onHashChange);
      window.removeEventListener('tript:navigate', onNativeNavigation);
    };
  }, []);

  // Register the listener before announcing readiness so queued tray commands are not lost.
  useEffect(() => {
    (window as PhotinoShellWindow).external?.sendMessage?.('tript:ready');
  }, []);

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
      setPlayerReturnRoute('library');
      setRoute('player');
    },
    [],
  );

  const closePlayer = useCallback(() => {
    setPlayerItem(null);
    setPlayerTitle('');
    setPlayerNavigation([]);
    setRoute((current) => (current === 'player' ? playerReturnRoute : current));
  }, [playerReturnRoute]);

  const openSessionReview = useCallback((recording: ContentItem) => {
    const clips = items.filter((item) => item.automated && item.sourceSessionPath === recording.filePath);
    setSessionReview({ recording, clips });
    setRoute('session');
  }, [items]);

  const openSessionClip = useCallback((item: ContentItem, navigation: ContentItem[]) => {
    setPlayerItem(item);
    setPlayerTitle(itemLabel(item));
    setPlayerNavigation(navigation);
    setPlayerReturnRoute('session');
    setRoute('player');
  }, []);

  const deletePlayerItem = useCallback((item: ContentItem) => {
    if (!item.automated) {
      return;
    }

    client.send('DeleteContent', { contentType: item.contentType, fileName: item.filePath });
    const remaining = playerNavigation.filter((candidate) => candidate.filePath !== item.filePath);
    const deletedIndex = playerNavigation.findIndex((candidate) => candidate.filePath === item.filePath);
    const replacement = remaining[deletedIndex] ?? remaining[deletedIndex - 1];
    setPlayerNavigation(remaining);
    if (replacement) {
      setPlayerItem(replacement);
      setPlayerTitle(itemLabel(replacement));
    } else {
      setPlayerItem(null);
      setPlayerTitle('');
      setRoute(playerReturnRoute);
    }
  }, [client, playerNavigation, playerReturnRoute]);

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

  useEffect(() => {
    setSessionReview((previous) => {
      if (!previous) {
        return previous;
      }
      const recording = items.find((item) => item.filePath === previous.recording.filePath);
      if (!recording) {
        return null;
      }

      // Content pushes are the live source of truth. Rebuild the highlight list instead of only
      // pruning deleted clips, because automatic generation adds clips after this page is already open.
      const clips = items.filter(
        (item) => item.automated && item.sourceSessionPath === recording.filePath,
      );
      return { recording, clips };
    });
  }, [items]);

  // Leaving for another destination closes whatever was open in the player route.
  const leaveFor = useCallback((next: Route) => {
    setRoute(next);
    setPlayerItem(null);
    if (next !== 'session') {
      setSessionReview(null);
    }
  }, []);
  const showLibrary = useCallback(() => {
    replaceRouteHash('library');
    leaveFor('library');
  }, [leaveFor]);
  const showSettings = useCallback(() => {
    replaceRouteHash('settings');
    leaveFor('settings');
  }, [leaveFor]);
  const showTraining = useCallback(() => leaveFor('training'), [leaveFor]);
  const backFromPlayer = useCallback(() => {
    if (playerReturnRoute === 'session') {
      setPlayerItem(null);
      setPlayerTitle('');
      setPlayerNavigation([]);
      setRoute('session');
      return;
    }
    showLibrary();
  }, [playerReturnRoute, showLibrary]);

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
            className={route === 'library' || route === 'player' || route === 'session' ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'library' || route === 'player' || route === 'session' ? 'page' : undefined}
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
          {(route === 'library' || route === 'player' || route === 'session') && (
            // Mounted but hidden while the player route is up. Unmounting would lose the user's
            // filters, sort, page and scroll while the player is open.
            <div hidden={route === 'player' || route === 'session'}>
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
              onBack={backFromPlayer}
              onDelete={deletePlayerItem}
              onReviewSession={openSessionReview}
              convertHdrClipsToSdr={convertHdrClipsToSdr}
              highlightCount={items.filter(
                (candidate) => candidate.automated && candidate.sourceSessionPath === playerItem.filePath,
              ).length}
              onItemChange={(item) => setPlayerTitle(itemLabel(item))}
            />
          )}
          {route === 'session' && sessionReview && (
            <SessionClipsView
              recording={sessionReview.recording}
              clips={sessionReview.clips}
              client={client}
              onBack={showLibrary}
              onOpen={openSessionClip}
            />
          )}
          {route === 'settings' && <SettingsView client={client} />}
          {route === 'training' && trainingEnabled && <TrainingView client={client} />}
        </div>
      </main>
    </div>
  );
}

function readStartupRoute(): Route {
  const hash = window.location.hash.slice(1).toLowerCase();
  if (hash === 'settings' || hash.startsWith('settings-')) {
      return 'settings';
  }
  return 'library';
}

function replaceRouteHash(route: 'library' | 'settings'): void {
  const hash = `#${route}`;
  if (window.location.hash === hash) {
    return;
  }
  window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`);
}
