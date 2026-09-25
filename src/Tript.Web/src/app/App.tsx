// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ToastProvider } from '../components/ui/toast/ToastProvider';
import { ErrorToasts } from '../components/toasts/ErrorToasts';
import { WarningToasts } from '../components/toasts/WarningToasts';
import { ConnectionToasts } from '../components/toasts/ConnectionToasts';
import { DisplayFallbackToasts } from '../components/toasts/DisplayFallbackToasts';
import { GameCandidateToasts } from '../components/toasts/GameCandidateToasts';
import { GameAddedToasts } from '../components/toasts/GameAddedToasts';
import { UpdateToasts } from '../components/toasts/UpdateToasts';
import { StorageToasts } from '../components/toasts/StorageToasts';
import { useStorage } from '../components/storage/useStorage';
import { useToast } from '../components/ui/toast/ToastProvider';
import { LibraryView } from '../components/LibraryView';
import { SessionsView } from '../components/SessionsView';
import { SessionClipsView } from '../components/SessionClipsView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import type { SettingsPageName } from '../settings/useSettings';
import { StreamerView } from '../components/StreamerView';
import { useTrash } from '../components/trash/useTrash';
import { sourceSession } from '../components/library/libraryModel';
import { ConfirmDeleteDialog } from '../components/library/ConfirmDeleteDialog';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import { hasSessionToken } from '../ipc/sessionToken';
import { useHostReachability } from './useHostReachability';
import { useWindowVisible } from './useWindowVisible';
import { useAppNavigation } from './useAppNavigation';
import { useClipJobs, type ClipJobResult } from './useClipJobs';
import { useDeleteFlow } from './useDeleteFlow';
import { useHostPreferences } from './useHostPreferences';
import { PlatformCapabilitiesContext, useHostPlatformCapabilities } from './platformCapabilities';
import { TripwireMark } from '../components/TripwireMark';
import { Icon } from '../components/ui/Icon';
import { trainingEnabled } from '../buildFeatures';
import { TrainingView } from '../components/TrainingView';
import './app.css';

export type { Route } from './useAppNavigation';

type PhotinoShellWindow = Window & {
  external?: {
    sendMessage?: (message: string) => void;
  };
};

export function App({
  ipcOptions,
  trainingFeatureEnabled = trainingEnabled,
}: {
  ipcOptions?: IpcClientOptions;
  trainingFeatureEnabled?: boolean;
}) {
  if (!hasSessionToken()) {
    return <MissingKeyNotice />;
  }
  return (
    <ToastProvider>
      <AppShell ipcOptions={ipcOptions} trainingFeatureEnabled={trainingFeatureEnabled} />
    </ToastProvider>
  );
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

function AppShell({
  ipcOptions,
  trainingFeatureEnabled,
}: {
  ipcOptions?: IpcClientOptions;
  trainingFeatureEnabled: boolean;
}) {
  const { client, connectionState } = useIpcClient(ipcOptions);
  const reachability = useHostReachability(connectionState);
  const windowVisible = useWindowVisible(client);
  const preferences = useHostPreferences(client, connectionState);
  const capabilities = useHostPlatformCapabilities(client);

  useEffect(() => {
    (window as PhotinoShellWindow).external?.sendMessage?.('tript:ready');
  }, []);

  const source = useIpcSessionSource(client);
  const { items, loaded } = useSessionSource(source);
  const itemsRef = useRef(items);
  itemsRef.current = items;

  const contentRef = useRef<HTMLDivElement>(null);
  const navigation = useAppNavigation(items, contentRef);
  const {
    route,
    routeRef,
    playerItem,
    playerTitle,
    playerNavigation,
    playerReturnRoute,
    sessionReview,
    sessionReturnRoute,
    openInPlayer,
    adoptPlayerItem,
    showSettings,
  } = navigation;

  const showLibrary = navigation.showLibrary;
  useEffect(() => {
    if (route === 'streamer' && !capabilities.obsSharing) {
      showLibrary();
    }
  }, [route, capabilities.obsSharing, showLibrary]);

  const trash = useTrash(client);
  const deletion = useDeleteFlow({
    client,
    items,
    trash,
    navigation,
    deleteLinkedHighlightsByDefault: preferences.deleteLinkedHighlightsByDefault,
  });
  const { push, dismiss } = useToast();
  const [gameSettingsFocus, setGameSettingsFocus] = useState<string | null>(null);
  const [settingsPageFocus, setSettingsPageFocus] = useState<SettingsPageName | null>(null);

  const toggleFavorite = useCallback((item: ContentItem) => {
    if (item.highlightsOnly === true) {
      return;
    }
    client.send('ToggleFavorite', {
      contentType: item.contentType,
      filePath: item.filePath,
      favorite: item.favorite !== true,
    });
  }, [client]);

  const notifyClipCreated = useCallback((result: ClipJobResult) => {
    const clip = result.item;
    if (clip) {
      push({
        key: `clip-created-${clip.filePath}`,
        kind: 'success',
        message: `Created "${result.title}".`,
        actions: [{
          label: 'View',
          onClick: () => {
            dismiss(`clip-created-${clip.filePath}`);
            const currentItems = itemsRef.current;
            const fresh = currentItems.find((candidate) => candidate.filePath === clip.filePath);
            const target = fresh ?? clip;
            if (routeRef.current === 'player') {
              adoptPlayerItem(target);
            } else {
              openInPlayer(target, currentItems);
            }
          },
        }],
      });
      return;
    }
    push({
      kind: 'error',
      message: `Creating "${result.title}" failed.`,
      note: result.error,
    });
  }, [push, dismiss, adoptPlayerItem, openInPlayer, routeRef]);

  const { clipJobCount, enqueueClip } = useClipJobs(client, connectionState, notifyClipCreated);
  const storage = useStorage(client);

  const openGameSettings = useCallback((gameId: string) => {
    setGameSettingsFocus(gameId);
    showSettings();
  }, [showSettings]);

  const openStorageSettings = useCallback(() => {
    setSettingsPageFocus('storage');
    showSettings();
  }, [showSettings]);
  const playerSession = playerItem ? sourceSession(playerItem, items) : null;
  const libraryActive = route === 'library' || route === 'session'
    || (route === 'player' && playerReturnRoute !== 'sessions');
  const sessionsActive = route === 'sessions' || (route === 'player' && playerReturnRoute === 'sessions');

  return (
    <PlatformCapabilitiesContext.Provider value={capabilities}>
    <div className="app-shell" data-window-hidden={windowVisible ? undefined : ''}>
      <header className="app-topbar">
        <div className="app-brand">
          <TripwireMark size={26} />
          <strong>Tript</strong>
        </div>
        <nav className="app-nav" aria-label="Primary">
          <button
            type="button"
            className={libraryActive ? 'nav-item active' : 'nav-item'}
            aria-current={libraryActive ? 'page' : undefined}
            onClick={navigation.showLibrary}
          >
            <Icon name="library" className="nav-glyph" />
            <span>Library</span>
          </button>
          <button
            type="button"
            className={sessionsActive ? 'nav-item active' : 'nav-item'}
            aria-current={sessionsActive ? 'page' : undefined}
            onClick={navigation.showSessions}
          >
            <Icon name="monitor" className="nav-glyph" />
            <span>Sessions</span>
          </button>
          {capabilities.obsSharing && (
          <button
            type="button"
            className={route === 'streamer' ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'streamer' ? 'page' : undefined}
            onClick={navigation.showStreamer}
          >
            <Icon name="broadcast" className="nav-glyph" />
            <span>Streamer</span>
          </button>
          )}
          <button
            type="button"
            className={route === 'settings' ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'settings' ? 'page' : undefined}
            onClick={showSettings}
          >
            <Icon name="settings" className="nav-glyph" />
            <span>Settings</span>
          </button>
          {trainingFeatureEnabled && (
            <button
              type="button"
              className={route === 'training' ? 'nav-item active' : 'nav-item'}
              aria-current={route === 'training' ? 'page' : undefined}
              onClick={navigation.showTraining}
            >
              <Icon name="clip" className="nav-glyph" />
              <span>Training</span>
            </button>
          )}
        </nav>
        {route === 'player' && <span className="app-topbar-context">{playerTitle}</span>}
        <RecorderBar
          client={client}
          connectionState={connectionState}
          trainingFeatureEnabled={trainingFeatureEnabled}
          clipJobCount={clipJobCount}
          windowVisible={windowVisible}
        />
      </header>
      <main className="app-main">
        <ConnectionToasts reachability={reachability} />
        <ErrorToasts client={client} />
        <WarningToasts client={client} />
        <DisplayFallbackToasts client={client} />
        <GameCandidateToasts client={client} />
        <GameAddedToasts client={client} onOpenGameSettings={openGameSettings} />
        <UpdateToasts client={client} />
        <StorageToasts client={client} onOpenStorageSettings={openStorageSettings} />
        <div
          className={route === 'player' ? 'app-content app-content-player' : 'app-content'}
          ref={contentRef}
        >
          {(route === 'library' || route === 'player' || route === 'session') && (
            <div hidden={route === 'player' || route === 'session'}>
              <LibraryView
                client={client}
                items={items}
                thumbnailLoadingActive={route === 'library'}
                connectionState={connectionState}
                contentLoaded={loaded}
                onOpen={(item, results, origin, rebuild) => openInPlayer(item, results, origin, 'library', rebuild)}
                retentionHours={trash.retentionHours}
                deleteLinkedHighlightsByDefault={preferences.deleteLinkedHighlightsByDefault}
                trash={trash}
                storageStatus={storage.status}
                onOpenStorageSettings={openStorageSettings}
              />
            </div>
          )}
          {route === 'player' && playerItem && (
            <PlayerView
              client={client}
              trainingEnabled={trainingFeatureEnabled}
              source={source}
              item={playerItem}
              navigationItems={playerNavigation}
              onBack={navigation.backFromPlayer}
              onDelete={deletion.requestPlayerDelete}
              onToggleFavorite={toggleFavorite}
              onReviewSession={navigation.openSessionReview}
              convertHdrClipsToSdr={preferences.convertHdrClipsToSdr}
              recording={preferences.recording}
              windowVisible={windowVisible}
              reviewRecording={playerSession ?? undefined}
              highlightCount={playerSession
                ? items.filter((candidate) => candidate.automated && candidate.sourceSessionPath === playerSession.filePath).length
                : 0}
              onItemChange={adoptPlayerItem}
              onCreateClip={enqueueClip}
            />
          )}
          {route === 'session' && sessionReview && (
            <SessionClipsView
              recording={sessionReview.recording}
              clips={sessionReview.clips}
              client={client}
              onBack={navigation.backFromSession}
              backLabel={sessionReturnRoute === 'player' ? 'Back to session' : 'Back to library'}
              onOpen={navigation.openSessionClip}
              onToggleFavorite={toggleFavorite}
              onDelete={deletion.requestSessionDelete}
            />
          )}
          {(route === 'sessions' || (route === 'player' && playerReturnRoute === 'sessions')) && (
            <div hidden={route === 'player'}>
              <SessionsView
                client={client}
                items={items}
                thumbnailLoadingActive={route === 'sessions'}
                connectionState={connectionState}
                contentLoaded={loaded}
                onOpen={navigation.openFromSessions}
                retentionHours={trash.retentionHours}
                deleteLinkedHighlightsByDefault={preferences.deleteLinkedHighlightsByDefault}
                storageStatus={storage.status}
                onOpenStorageSettings={openStorageSettings}
              />
            </div>
          )}
          {route === 'streamer' && capabilities.obsSharing && <StreamerView client={client} />}
          <div hidden={route !== 'settings'}>
            <SettingsView
              client={client}
              active={route === 'settings' && windowVisible}
              builtInGameIds={preferences.builtInGameIds}
              focusGameId={gameSettingsFocus}
              onFocusGameHandled={() => setGameSettingsFocus(null)}
              focusPage={settingsPageFocus}
              onFocusPageHandled={() => setSettingsPageFocus(null)}
            />
          </div>
          {route === 'training' && trainingFeatureEnabled && <TrainingView client={client} />}
        </div>
      </main>
      {deletion.deleteConfirmation && (
        <ConfirmDeleteDialog
          confirmation={deletion.deleteConfirmation}
          onCancel={deletion.cancelDelete}
          onConfirm={deletion.confirmDelete}
        />
      )}
    </div>
    </PlatformCapabilitiesContext.Provider>
  );
}
