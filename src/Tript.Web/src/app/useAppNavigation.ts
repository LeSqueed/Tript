// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react';
import { itemLabel, lacksMainVideo, sessionPlaylist } from '../components/library/libraryModel';
import type { ContentItem } from '../ipc/protocol';

export type Route = 'library' | 'sessions' | 'session' | 'streamer' | 'settings' | 'player' | 'training';

export type PlayerReturnRoute = 'library' | 'sessions' | 'session';

export interface SessionReview {
  recording: ContentItem;
  clips: ContentItem[];
}

interface SessionPlayerOrigin {
  recording: ContentItem;
  item: ContentItem;
  navigation: ContentItem[];
  returnRoute: PlayerReturnRoute;
}

export interface AppNavigation {
  route: Route;
  routeRef: RefObject<Route>;
  playerItem: ContentItem | null;
  playerTitle: string;
  playerNavigation: ContentItem[];
  playerReturnRoute: PlayerReturnRoute;
  sessionReview: SessionReview | null;
  sessionReturnRoute: 'library' | 'player';
  openInPlayer: (
    item: ContentItem,
    resultItems: ContentItem[],
    origin?: 'library' | 'session',
    returnRoute?: 'library' | 'sessions',
  ) => void;
  openFromSessions: (item: ContentItem, resultItems: ContentItem[]) => void;
  openSessionReview: (recording: ContentItem, returnRoute?: 'library' | 'player') => void;
  openSessionClip: (item: ContentItem, navigation: ContentItem[]) => void;
  adoptPlayerItem: (item: ContentItem) => void;
  advanceAfterPlayerDelete: (item: ContentItem) => void;
  showLibrary: () => void;
  showSessions: () => void;
  showStreamer: () => void;
  showSettings: () => void;
  showTraining: () => void;
  backFromPlayer: () => void;
  backFromSession: () => void;
}

export function useAppNavigation(
  items: ContentItem[],
  contentRef: RefObject<HTMLDivElement | null>,
): AppNavigation {
  const [route, setRoute] = useState<Route>(readStartupRoute);
  const routeRef = useRef(route);
  routeRef.current = route;
  const [playerItem, setPlayerItem] = useState<ContentItem | null>(null);
  const [playerTitle, setPlayerTitle] = useState('');
  const [playerNavigation, setPlayerNavigation] = useState<ContentItem[]>([]);
  const [playerReturnRoute, setPlayerReturnRoute] = useState<PlayerReturnRoute>('library');
  const [sessionReview, setSessionReview] = useState<SessionReview | null>(null);
  const [sessionPlayerOrigin, setSessionPlayerOrigin] = useState<SessionPlayerOrigin | null>(null);
  const [sessionReturnRoute, setSessionReturnRoute] = useState<'library' | 'player'>('library');
  const savedScrollTop = useRef(0);
  const overlayWasOpen = useRef(false);

  const clearPlayer = useCallback(() => {
    setPlayerItem(null);
    setPlayerTitle('');
    setPlayerNavigation([]);
  }, []);

  const showInPlayer = useCallback((item: ContentItem) => {
    setPlayerItem(item);
    setPlayerTitle(itemLabel(item));
  }, []);

  useEffect(() => {
    const onHashChange = () => {
      setRoute(readStartupRoute());
      clearPlayer();
    };
    const onNativeNavigation = (event: Event) => {
      if ((event as CustomEvent<string>).detail !== 'settings') {
        return;
      }
      replaceRouteHash('settings');
      setRoute('settings');
      clearPlayer();
    };
    window.addEventListener('hashchange', onHashChange);
    window.addEventListener('tript:navigate', onNativeNavigation);
    return () => {
      window.removeEventListener('hashchange', onHashChange);
      window.removeEventListener('tript:navigate', onNativeNavigation);
    };
  }, [clearPlayer]);

  const openSessionReview = useCallback((recording: ContentItem, returnRoute: 'library' | 'player' = 'player') => {
    const clips = items.filter((item) => item.automated && item.sourceSessionPath === recording.filePath);
    setSessionReview({ recording, clips });
    setSessionReturnRoute(returnRoute);
    setSessionPlayerOrigin(returnRoute === 'player'
      ? { recording, item: playerItem ?? recording, navigation: playerNavigation, returnRoute: playerReturnRoute }
      : null);
    setRoute('session');
  }, [items, playerItem, playerNavigation, playerReturnRoute]);

  const openInPlayer = useCallback(
    (
      item: ContentItem,
      resultItems: ContentItem[],
      origin: 'library' | 'session' = 'library',
      returnRoute: 'library' | 'sessions' = 'library',
    ) => {
      savedScrollTop.current = contentRef.current?.scrollTop ?? 0;
      const isSession = item.contentType === 'recording' || item.contentType === 'buffer';
      const recording = isSession && (origin === 'session' || lacksMainVideo(item)) ? item : null;
      const navigation = recording ? sessionPlaylist(recording, items) : resultItems;
      const requested = navigation.find((candidate) => candidate.filePath === item.filePath) ?? navigation[0];
      if (!requested) {
        if (recording) openSessionReview(recording, 'library');
        return;
      }
      showInPlayer(requested);
      setPlayerNavigation(navigation);
      setPlayerReturnRoute(returnRoute);
      setRoute('player');
    },
    [items, openSessionReview, contentRef, showInPlayer],
  );

  const openFromSessions = useCallback((item: ContentItem, resultItems: ContentItem[]) => {
    openInPlayer(item, resultItems, 'session', 'sessions');
  }, [openInPlayer]);

  const closePlayer = useCallback(() => {
    clearPlayer();
    setRoute((current) => (current === 'player' ? playerReturnRoute : current));
  }, [playerReturnRoute, clearPlayer]);

  const openSessionClip = useCallback((item: ContentItem, navigation: ContentItem[]) => {
    showInPlayer(item);
    setPlayerNavigation(navigation);
    setPlayerReturnRoute('session');
    setRoute('player');
  }, [showInPlayer]);

  const advanceAfterPlayerDelete = useCallback((item: ContentItem) => {
    const remaining = playerNavigation.filter((candidate) => candidate.filePath !== item.filePath);
    const deletedIndex = playerNavigation.findIndex((candidate) => candidate.filePath === item.filePath);
    const replacement = remaining[deletedIndex] ?? remaining[deletedIndex - 1];
    setPlayerNavigation(remaining);
    if (replacement) {
      showInPlayer(replacement);
    } else {
      setPlayerItem(null);
      setPlayerTitle('');
      setRoute(playerReturnRoute);
    }
  }, [playerNavigation, playerReturnRoute, showInPlayer]);

  useLayoutEffect(() => {
    if (playerItem !== null) {
      overlayWasOpen.current = true;
      return;
    }
    if (overlayWasOpen.current) {
      overlayWasOpen.current = false;
      if (contentRef.current) {
        contentRef.current.scrollTop = savedScrollTop.current;
      }
    }
  }, [playerItem, contentRef]);

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
    showInPlayer(currentItem);
    setPlayerNavigation((previous) =>
      previous
        .map((candidate) => byPath.get(candidate.filePath))
        .filter((entry): entry is ContentItem => entry !== undefined),
    );
  }, [items, playerItem, closePlayer, showInPlayer]);

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
      clearPlayer();
      setRoute((current) => current === 'player' || current === 'session' ? 'library' : current);
      return;
    }
    if (recording !== sessionPlayerOrigin.recording) {
      setSessionPlayerOrigin((previous) => previous ? { ...previous, recording } : null);
    }
  }, [items, sessionPlayerOrigin, clearPlayer]);

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
  const showStreamer = useCallback(() => {
    replaceRouteHash('streamer');
    leaveFor('streamer');
  }, [leaveFor]);
  const showSettings = useCallback(() => {
    replaceRouteHash('settings');
    leaveFor('settings');
  }, [leaveFor]);
  const showTraining = useCallback(() => leaveFor('training'), [leaveFor]);
  const backFromPlayer = useCallback(() => {
    if (playerReturnRoute === 'session') {
      clearPlayer();
      setRoute('session');
      return;
    }
    if (playerReturnRoute === 'sessions') {
      showSessions();
      return;
    }
    showLibrary();
  }, [playerReturnRoute, showLibrary, showSessions, clearPlayer]);
  const backFromSession = useCallback(() => {
    if (sessionReturnRoute === 'player' && sessionPlayerOrigin) {
      showInPlayer(sessionPlayerOrigin.item);
      setPlayerNavigation(sessionPlayerOrigin.navigation);
      setPlayerReturnRoute(sessionPlayerOrigin.returnRoute);
      setSessionReview(null);
      setSessionPlayerOrigin(null);
      setRoute('player');
      return;
    }
    showLibrary();
  }, [sessionPlayerOrigin, sessionReturnRoute, showLibrary, showInPlayer]);

  return {
    route,
    routeRef,
    playerItem,
    playerTitle,
    playerNavigation,
    playerReturnRoute,
    sessionReview,
    sessionReturnRoute,
    openInPlayer,
    openFromSessions,
    openSessionReview,
    openSessionClip,
    adoptPlayerItem: showInPlayer,
    advanceAfterPlayerDelete,
    showLibrary,
    showSessions,
    showStreamer,
    showSettings,
    showTraining,
    backFromPlayer,
    backFromSession,
  };
}

function readStartupRoute(): Route {
  const hash = window.location.hash.slice(1).toLowerCase();
  if (hash === 'settings' || hash.startsWith('settings-')) {
    return 'settings';
  }
  if (hash === 'sessions') {
    return 'sessions';
  }
  if (hash === 'streamer') {
    return 'streamer';
  }
  return 'library';
}

function replaceRouteHash(route: 'library' | 'sessions' | 'streamer' | 'settings'): void {
  const hash = `#${route}`;
  if (window.location.hash === hash) {
    return;
  }
  window.history.replaceState(null, '', `${window.location.pathname}${window.location.search}${hash}`);
}
