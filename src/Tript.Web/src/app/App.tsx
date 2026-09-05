// SPDX-License-Identifier: GPL-2.0-or-later
//
// The app shell: recorder bar on top, nav + content below, and a live connection status. Top-level
// pages (library, sessions, settings, training) plus the player and session-review overlays.

import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useIpcClient } from './useConnection';
import { RecorderBar } from '../components/RecorderBar';
import { ToastProvider } from '../components/ui/toast/ToastProvider';
import { ErrorToasts } from '../components/toasts/ErrorToasts';
import { WarningToasts } from '../components/toasts/WarningToasts';
import { ConnectionToasts } from '../components/toasts/ConnectionToasts';
import { DisplayFallbackToasts } from '../components/toasts/DisplayFallbackToasts';
import { GameCandidateToasts } from '../components/toasts/GameCandidateToasts';
import { useToast } from '../components/ui/toast/ToastProvider';
import { LibraryView } from '../components/LibraryView';
import { SessionsView } from '../components/SessionsView';
import { SessionClipsView } from '../components/SessionClipsView';
import { PlayerView } from '../components/PlayerView';
import { SettingsView } from '../components/SettingsView';
import { useTrash } from '../components/trash/useTrash';
import {
  cascadableLinkedHighlights,
  itemLabel,
  lacksMainVideo,
  sessionPlaylist,
  sourceSession,
} from '../components/library/libraryModel';
import { ConfirmDeleteDialog, type DeleteConfirmation } from '../components/library/ConfirmDeleteDialog';
import { useIpcSessionSource, useSessionSource } from '../components/player/useSessionSource';
import type { ContentItem, CreateClipParameters, GameInfo, ImportProgressMessage, RecordingState } from '../ipc/protocol';
import type { IpcClientOptions } from '../ipc/websocketClient';
import { hasSessionToken } from '../ipc/sessionToken';
import { useHostReachability } from './useHostReachability';
import { TripwireMark } from '../components/TripwireMark';
import { Icon } from '../components/ui/Icon';
import { trainingEnabled } from '../buildFeatures';
import { TrainingView } from '../components/TrainingView';
import './app.css';

export type Route = 'library' | 'sessions' | 'session' | 'settings' | 'player' | 'training';

const FALLBACK_BUILT_IN_GAME_IDS = ['Overwatch'] as const;

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
  // Without the launch token every listener refuses this page: the socket, the videos, the
  // thumbnails. Rendering the shell anyway would be an empty library over a socket reconnecting
  // forever, which reads as a broken backend. Say what is wrong and open nothing.
  if (!hasSessionToken()) {
    return <MissingKeyNotice />;
  }
  // The provider sits above the shell, not inside it: the shell's own restore toast needs useToast.
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
  // A key is present (App checked) but may no longer be accepted: the host mints a new one per
  // launch, so a tab left open across a restart 403s on everything and would otherwise just sit
  // there empty.
  const reachability = useHostReachability(connectionState);
  const startupRoute = readStartupRoute();
  const [route, setRoute] = useState<Route>(startupRoute);
  // The latest route, readable from a toast action that was pushed on another route: the clip's
  // View button may be clicked long after the player it was made in has closed.
  const routeRef = useRef(route);
  routeRef.current = route;
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);
  const [playerTitle, setPlayerTitle] = useState('');
  const [playerNavigation, setPlayerNavigation] = useState<ContentItem[]>([]);
  const [playerReturnRoute, setPlayerReturnRoute] = useState<'library' | 'sessions' | 'session'>('library');
  const [sessionReview, setSessionReview] = useState<{ recording: ContentItem; clips: ContentItem[] } | null>(null);
  const [sessionPlayerOrigin, setSessionPlayerOrigin] = useState<{
    recording: ContentItem;
    item: ContentItem;
    navigation: ContentItem[];
  } | null>(null);
  const [sessionReturnRoute, setSessionReturnRoute] = useState<'library' | 'player'>('library');
  const [convertHdrClipsToSdr, setConvertHdrClipsToSdr] = useState(false);
  const [deleteLinkedHighlightsByDefault, setDeleteLinkedHighlightsByDefault] = useState(false);
  const [recording, setRecording] = useState(false);
  const [pendingDelete, setPendingDelete] = useState<{ item: ContentItem; advancePlayer: boolean } | null>(null);
  const [builtInGameIds, setBuiltInGameIds] = useState<readonly string[]>(FALLBACK_BUILT_IN_GAME_IDS);
  const [clipJobCount, setClipJobCount] = useState(0);
  const queuedClipJobs = useRef<CreateClipParameters[]>([]);
  const activeClipJob = useRef<CreateClipParameters | null>(null);

  useEffect(() => {
    const remove = client.on('gameList', (content) => {
      const list = Array.isArray(content) ? (content as GameInfo[]) : [];
      const builtIn = list
        .filter((game) => game.builtIn === true && typeof game.id === 'string')
        .map((game) => String(game.id));
      if (builtIn.length > 0) {
        setBuiltInGameIds(builtIn);
      }
    });
    if (connectionState === 'connected') {
      client.send('ListGames');
    }
    return remove;
  }, [client, connectionState]);

  useEffect(() => {
    const remove = client.on('settings', (content) => {
      const settings = (content as {
        settings?: {
          general?: { convertHdrClipsToSdr?: boolean };
          recording?: { deleteLinkedHighlightsByDefault?: boolean };
        };
      }).settings;
      setConvertHdrClipsToSdr(settings?.general?.convertHdrClipsToSdr === true);
      setDeleteLinkedHighlightsByDefault(
        settings?.recording?.deleteLinkedHighlightsByDefault === true,
      );
    });
    client.send('ListSettings');
    return remove;
  }, [client]);

  useEffect(() => client.on('state', (content) => {
    const state = (content as { state?: RecordingState }).state;
    if (state)
      setRecording(state.recording === true);
  }), [client]);

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
  const itemsRef = useRef(items);
  itemsRef.current = items;

  // Owned by the shell, not by the trash screen: the library's delete confirmation quotes
  // `retentionHours`, and it must be right the first time a user deletes anything.
  const trash = useTrash(client);
  const { push, dismiss } = useToast();
  const [pendingRestore, setPendingRestore] = useState<ContentItem | null>(null);
  const restoreSentRef = useRef(false);

  // The scrolling content column. Save its position while the library remains mounted under the
  // player route so returning to Library restores the user's place.
  const contentRef = useRef<HTMLDivElement>(null);
  const savedScrollTop = useRef(0);
  const overlayWasOpen = useRef(false);

  const openSessionReview = useCallback((recording: ContentItem, returnRoute: 'library' | 'player' = 'player') => {
    const clips = items.filter((item) => item.automated && item.sourceSessionPath === recording.filePath);
    setSessionReview({ recording, clips });
    setSessionReturnRoute(returnRoute);
    setSessionPlayerOrigin(returnRoute === 'player'
      ? { recording, item: playerItem ?? recording, navigation: playerNavigation }
      : null);
    setRoute('session');
  }, [items, playerItem, playerNavigation]);

  // The player's playlist depends on what the user opened, not just what they opened it from: a
  // session entry point (the recent shelf, the sessions page) plays that session's own list — main
  // video plus its highlights in order — while a library card plays the result list it sits in, even
  // when the card is a session. `returnRoute` is where back lands; the sessions page keeps its own
  // tab, the library's review overlay does not.
  const openInPlayer = useCallback(
    (
      item: ContentItem,
      resultItems: ContentItem[],
      origin: 'library' | 'session' = 'library',
      returnRoute: 'library' | 'sessions' = 'library',
    ) => {
      savedScrollTop.current = contentRef.current?.scrollTop ?? 0;
      const isSession = item.contentType === 'recording' || item.contentType === 'buffer';
      // A placeholder card has no main video to open, so even a library entry falls back to the
      // session's own list: the first playable child, or the review when there is none.
      const recording = isSession && (origin === 'session' || lacksMainVideo(item)) ? item : null;
      const navigation = recording ? sessionPlaylist(recording, items) : resultItems;
      const requested = navigation.find((candidate) => candidate.filePath === item.filePath) ?? navigation[0];
      if (!requested) {
        if (recording) openSessionReview(recording, 'library');
        return;
      }
      setPlayerItem(requested);
      setPlayerTitle(itemLabel(requested));
      setPlayerNavigation(navigation);
      setPlayerReturnRoute(returnRoute);
      setRoute('player');
    },
    [items, openSessionReview],
  );

  const openFromSessions = useCallback((item: ContentItem, resultItems: ContentItem[]) => {
    openInPlayer(item, resultItems, 'session', 'sessions');
  }, [openInPlayer]);

  const closePlayer = useCallback(() => {
    setPlayerItem(null);
    setPlayerTitle('');
    setPlayerNavigation([]);
    setRoute((current) => (current === 'player' ? playerReturnRoute : current));
  }, [playerReturnRoute]);

  const openSessionClip = useCallback((item: ContentItem, navigation: ContentItem[]) => {
    setPlayerItem(item);
    setPlayerTitle(itemLabel(item));
    setPlayerNavigation(navigation);
    setPlayerReturnRoute('session');
    setRoute('player');
  }, []);

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

  const adoptPlayerItem = useCallback((item: ContentItem) => {
    setPlayerItem(item);
    setPlayerTitle(itemLabel(item));
  }, []);

  const notifyClipCreated = useCallback((result: { title: string; item?: ContentItem; error?: string }) => {
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
  }, [push, dismiss, adoptPlayerItem, openInPlayer]);

  const startNextClip = useCallback(() => {
    if (activeClipJob.current !== null) {
      return;
    }
    const next = queuedClipJobs.current.shift();
    if (next !== undefined) {
      activeClipJob.current = next;
      client.send('CreateClip', next);
    }
  }, [client]);

  const enqueueClip = useCallback((parameters: CreateClipParameters) => {
    if (connectionState !== 'connected') {
      notifyClipCreated({ title: parameters.title, error: 'Tript is not connected.' });
      return;
    }
    queuedClipJobs.current.push(parameters);
    setClipJobCount(queuedClipJobs.current.length + (activeClipJob.current === null ? 0 : 1));
    startNextClip();
  }, [connectionState, notifyClipCreated, startNextClip]);

  useEffect(() => client.on('importProgress', (content) => {
    const message = content as ImportProgressMessage;
    const active = activeClipJob.current;
    if (active === null || message.id !== active.id
      || (message.status !== 'done' && message.status !== 'error')) {
      return;
    }

    activeClipJob.current = null;
    notifyClipCreated(message.status === 'done'
      ? { title: active.title, item: message.content }
      : { title: active.title, error: message.error ?? 'Clip creation failed' });
    startNextClip();
    setClipJobCount(queuedClipJobs.current.length + (activeClipJob.current === null ? 0 : 1));
  }), [client, notifyClipCreated, startNextClip]);

  useEffect(() => {
    if (connectionState === 'connected' || clipJobCount === 0) {
      return;
    }
    const interrupted = [activeClipJob.current, ...queuedClipJobs.current]
      .filter((job): job is CreateClipParameters => job !== null);
    activeClipJob.current = null;
    queuedClipJobs.current = [];
    setClipJobCount(0);
    for (const job of interrupted) {
      notifyClipCreated({ title: job.title, error: 'The connection was lost during clip creation.' });
    }
  }, [connectionState, clipJobCount, notifyClipCreated]);

  const advanceAfterPlayerDelete = useCallback((item: ContentItem) => {
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
  }, [playerNavigation, playerReturnRoute]);

  const deleteItem = useCallback((
    item: ContentItem,
    permanent: boolean,
    deleteLinkedHighlights: boolean,
    advancePlayer: boolean,
  ) => {
    client.send('DeleteContent', {
      contentType: item.contentType,
      fileName: item.filePath,
      ...(permanent ? { permanent: true } : {}),
      ...(item.contentType === 'recording' && deleteLinkedHighlights
        ? { deleteLinkedHighlights: true }
        : {}),
    });
    if (advancePlayer) {
      advanceAfterPlayerDelete(item);
    }
  }, [client, advanceAfterPlayerDelete]);

  const beginRestore = useCallback((item: ContentItem) => {
    restoreSentRef.current = false;
    setPendingRestore(item);
  }, []);

  const requestPlayerDelete = useCallback((item: ContentItem) => {
    if (playerReturnRoute === 'session') {
      deleteItem(item, false, false, true);
      // Highlights delete without a confirmation on purpose, so the toast is the acknowledgement:
      // the message says where the item went, and its action is the undo for the one slip.
      push({
        key: `trashed-${item.filePath}`,
        kind: 'success',
        message: `Moved "${itemLabel(item)}" to trash.`,
        duration: 10_000,
        actions: [{ label: 'Restore', onClick: () => beginRestore(item) }],
      });
      return;
    }
    setPendingDelete({ item, advancePlayer: true });
  }, [deleteItem, playerReturnRoute, push, beginRestore]);

  const requestSessionDelete = useCallback((item: ContentItem) => {
    setPendingDelete({ item, advancePlayer: false });
  }, []);

  const confirmDelete = useCallback((permanent: boolean, deleteLinkedHighlights: boolean) => {
    if (pendingDelete) {
      deleteItem(
        pendingDelete.item,
        permanent,
        deleteLinkedHighlights,
        pendingDelete.advancePlayer,
      );
    }
    setPendingDelete(null);
  }, [deleteItem, pendingDelete]);

  const deleteConfirmation: DeleteConfirmation | null = pendingDelete ? (() => {
    const item = pendingDelete.item;
    const isRecording = item.contentType === 'recording';
    const cascadable = isRecording ? cascadableLinkedHighlights(item, items).length : 0;
    return {
      title: `Delete ${itemLabel(item)}?`,
      names: [itemLabel(item)],
      confirmLabel: 'Move to trash',
      // A missing-video placeholder names itself but has no session file to move; only its cascaded
      // highlights (if any) actually go to the trash, so the sentence must not count the session.
      affectedCount: isRecording && (item.videoMissing === true || item.highlightsOnly === true) ? 0 : 1,
      ...(isRecording ? { cascadeCount: cascadable } : {}),
      ...(isRecording
        ? {
            checkbox: {
              label: 'Delete linked highlights (favourited highlights are kept)',
              defaultChecked: deleteLinkedHighlightsByDefault,
            },
          }
        : {}),
      retentionHours: trash.retentionHours,
    };
  })() : null;

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

  // One map per push, not a `find` per playlist entry: a whole-library playlist against a
  // whole-library content list is the 9k × 9k case this effect must not make.
  useEffect(() => {
    if (!playerItem) {
      return;
    }
    const byPath = new Map<string, ContentItem>();
    for (const entry of items) {
      if (!byPath.has(entry.filePath)) {
        byPath.set(entry.filePath, entry);
      }
    }
    const currentItem = byPath.get(playerItem.filePath);
    if (!currentItem) {
      closePlayer();
      return;
    }
    setPlayerItem(currentItem);
    setPlayerTitle(itemLabel(currentItem));
    setPlayerNavigation((previous) =>
      previous
        .map((candidate) => byPath.get(candidate.filePath))
        .filter((entry): entry is ContentItem => entry !== undefined),
    );
  }, [items, playerItem, closePlayer]);

  // The click may land before the backend's `trash` push that follows the delete, and only that push
  // names the entry RestoreTrash needs. Send the restore the first time the entry is seen.
  //
  // The wire carries the BARE file name, not the relative path the content list uses, so match on
  // that. The list is newest-first, so the first hit is the item just deleted even if an older entry
  // happens to share its name.
  useEffect(() => {
    if (pendingRestore === null || restoreSentRef.current) {
      return;
    }
    const entry = trash.entries.find(
      (candidate) =>
        candidate.contentType === pendingRestore.contentType
        && candidate.fileName === pendingRestore.fileName,
    );
    if (entry !== undefined) {
      restoreSentRef.current = true;
      trash.restore([entry.id]);
    }
  }, [pendingRestore, trash]);

  // The restore is done when the item is back in the content. Open it only then: the player
  // closes itself on any item the list does not have, so landing early would immediately bounce.
  //
  // Land where the item came from: with the session review still open, the player's back returns
  // to that session's highlight list. Leaving for the library has already closed the review, so
  // the library route is the fallback for anything the open review does not own.
  useEffect(() => {
    if (pendingRestore === null || !restoreSentRef.current) {
      return;
    }
    const restored = items.find((candidate) => candidate.filePath === pendingRestore.filePath);
    if (restored !== undefined) {
      setPendingRestore(null);
      dismiss(`trashed-${restored.filePath}`);
      // Membership is judged against the live content list, not the review's cached clip array:
      // the push that restores the item rebuilds that array a beat AFTER this effect has run.
      if (sessionReview !== null
        && restored.automated === true
        && restored.sourceSessionPath === sessionReview.recording.filePath) {
        const clips = items
          .filter((candidate) => candidate.automated && candidate.sourceSessionPath === sessionReview.recording.filePath)
          .sort((left, right) => (left.clipStartTime ?? Number.POSITIVE_INFINITY) - (right.clipStartTime ?? Number.POSITIVE_INFINITY));
        openSessionClip(restored, clips);
      } else {
        openInPlayer(restored, items);
      }
    }
  }, [pendingRestore, items, dismiss, openInPlayer, openSessionClip, sessionReview]);

  useEffect(() => {
    if (sessionReview
      && !items.some((item) => item.filePath === sessionReview.recording.filePath)) {
      setRoute((current) => current === 'session' ? 'library' : current);
    }
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

  useEffect(() => {
    if (!sessionPlayerOrigin) {
      return;
    }
    const recording = items.find((item) => item.filePath === sessionPlayerOrigin.recording.filePath);
    if (!recording) {
      setSessionPlayerOrigin(null);
      setSessionReview(null);
      setPlayerItem(null);
      setPlayerTitle('');
      setPlayerNavigation([]);
      setRoute((current) => current === 'player' || current === 'session' ? 'library' : current);
      return;
    }
    if (recording !== sessionPlayerOrigin.recording) {
      setSessionPlayerOrigin((previous) => previous ? { ...previous, recording } : null);
    }
  }, [items, sessionPlayerOrigin]);

  // Leaving for another destination closes whatever was open in the player route.
  const leaveFor = useCallback((next: Route) => {
    setRoute(next);
    setPlayerItem(null);
    if (next !== 'session') {
      setSessionReview(null);
      setSessionPlayerOrigin(null);
    }
  }, []);
  const showLibrary = useCallback(() => {
    replaceRouteHash('library');
    leaveFor('library');
  }, [leaveFor]);
  const showSessions = useCallback(() => {
    replaceRouteHash('sessions');
    leaveFor('sessions');
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
    if (playerReturnRoute === 'sessions') {
      showSessions();
      return;
    }
    showLibrary();
  }, [playerReturnRoute, showLibrary, showSessions]);
  const backFromSession = useCallback(() => {
    if (sessionReturnRoute === 'player' && sessionPlayerOrigin) {
      setPlayerItem(sessionPlayerOrigin.item);
      setPlayerTitle(itemLabel(sessionPlayerOrigin.item));
      setPlayerNavigation(sessionPlayerOrigin.navigation);
      setSessionReview(null);
      setSessionPlayerOrigin(null);
      setRoute('player');
      return;
    }
    showLibrary();
  }, [sessionPlayerOrigin, sessionReturnRoute, showLibrary]);
  const playerSession = playerItem ? sourceSession(playerItem, items) : null;

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
            className={route === 'library' || route === 'session' || (route === 'player' && playerReturnRoute !== 'sessions') ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'library' || route === 'session' || (route === 'player' && playerReturnRoute !== 'sessions') ? 'page' : undefined}
            onClick={showLibrary}
          >
            <Icon name="library" className="nav-glyph" />
            <span>Library</span>
          </button>
          <button
            type="button"
            className={route === 'sessions' || (route === 'player' && playerReturnRoute === 'sessions') ? 'nav-item active' : 'nav-item'}
            aria-current={route === 'sessions' || (route === 'player' && playerReturnRoute === 'sessions') ? 'page' : undefined}
            onClick={showSessions}
          >
            <Icon name="monitor" className="nav-glyph" />
            <span>Sessions</span>
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
          {trainingFeatureEnabled && (
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
        <RecorderBar
          client={client}
          connectionState={connectionState}
          trainingFeatureEnabled={trainingFeatureEnabled}
          clipJobCount={clipJobCount}
        />
      </header>
      <main className="app-main">
        <ConnectionToasts reachability={reachability} />
        <ErrorToasts client={client} />
        <WarningToasts client={client} />
        <DisplayFallbackToasts client={client} />
        <GameCandidateToasts client={client} />
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
                thumbnailLoadingActive={route === 'library'}
                connectionState={connectionState}
                contentLoaded={loaded}
                onOpen={openInPlayer}
                retentionHours={trash.retentionHours}
                deleteLinkedHighlightsByDefault={deleteLinkedHighlightsByDefault}
                trash={trash}
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
              onBack={backFromPlayer}
              onDelete={requestPlayerDelete}
              onToggleFavorite={toggleFavorite}
              onReviewSession={openSessionReview}
              convertHdrClipsToSdr={convertHdrClipsToSdr}
              recording={recording}
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
              onBack={backFromSession}
              backLabel={sessionReturnRoute === 'player' ? 'Back to session' : 'Back to library'}
              onOpen={openSessionClip}
              onToggleFavorite={toggleFavorite}
              onDelete={requestSessionDelete}
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
                onOpen={openFromSessions}
                retentionHours={trash.retentionHours}
                deleteLinkedHighlightsByDefault={deleteLinkedHighlightsByDefault}
              />
            </div>
          )}
          {route === 'settings' && <SettingsView client={client} builtInGameIds={builtInGameIds} />}
          {route === 'training' && trainingFeatureEnabled && <TrainingView client={client} />}
        </div>
      </main>
      {deleteConfirmation && (
        <ConfirmDeleteDialog
          confirmation={deleteConfirmation}
          onCancel={() => setPendingDelete(null)}
          onConfirm={confirmDelete}
        />
      )}
      </div>
  );
}

function readStartupRoute(): Route {
  const hash = window.location.hash.slice(1).toLowerCase();
  if (hash === 'settings' || hash.startsWith('settings-')) {
      return 'settings';
  }
  if (hash === 'sessions') {
    return 'sessions';
  }
  return 'library';
}

function replaceRouteHash(route: 'library' | 'sessions' | 'settings'): void {
  const hash = `#${route}`;
  if (window.location.hash === hash) {
    return;
  }
  window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`);
}
