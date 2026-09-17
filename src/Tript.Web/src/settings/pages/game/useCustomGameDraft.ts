// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import type {
  GameSearchResult,
  GameSearchResultsMessage,
  ResolvedGameSearchMessage,
  SelectedGameExecutableMessage,
  SettingsUpdateResultMessage,
} from '../../../ipc/protocol';
import type { GameSetting } from '../../settingsModel';

export interface CustomGameDraft {
  index: number | null;
  name: string;
  executablePath: string;
  selectedGame: GameSearchResult | null;
}

export interface CustomGameDraftState {
  draft: CustomGameDraft | null;
  validationAttempted: boolean;
  saving: boolean;
  submissionError: string | null;
  searchQuery: string;
  searchResults: GameSearchResult[];
  searchError: string | null;
  searching: boolean;
  resolving: boolean;
  setName: (name: string) => void;
  setExecutablePath: (executablePath: string) => void;
  setSearchQuery: (query: string) => void;
  startAdd: () => void;
  startEdit: (index: number, game: GameSetting) => void;
  cancel: () => void;
  save: () => void;
  browse: () => void;
  search: () => void;
  selectResult: (result: GameSearchResult) => void;
}

export function isAbsoluteExecutablePath(path: string): boolean {
  return /^(?:[a-zA-Z]:[\\/]|\\\\|\/).+[^\\/]$/.test(path);
}

export function useCustomGameDraft({
  gameList,
  saveGameList,
  selectedGameExecutable,
  settingsUpdateResult,
  gameSearchResults,
  resolvedGameSearch,
  onBrowseExecutable,
  onSearchGames,
  onResolveGameSearch,
}: {
  gameList: GameSetting[];
  saveGameList: (gameList: GameSetting[]) => string;
  selectedGameExecutable: SelectedGameExecutableMessage | null;
  settingsUpdateResult: SettingsUpdateResultMessage | null;
  gameSearchResults: GameSearchResultsMessage | null;
  resolvedGameSearch: ResolvedGameSearchMessage | null;
  onBrowseExecutable: (requestId: string) => void;
  onSearchGames: (requestId: string, query: string) => void;
  onResolveGameSearch: (requestId: string, input: string) => void;
}): CustomGameDraftState {
  const [draft, setDraft] = useState<CustomGameDraft | null>(null);
  const [validationAttempted, setValidationAttempted] = useState(false);
  const [pendingSaveRequest, setPendingSaveRequest] = useState<string | null>(null);
  const [submissionError, setSubmissionError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<GameSearchResult[]>([]);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [pendingSearchRequest, setPendingSearchRequest] = useState<string | null>(null);
  const [pendingResolveRequest, setPendingResolveRequest] = useState<string | null>(null);
  const pendingBrowseRequest = useRef<string | null>(null);

  useEffect(() => {
    if (!selectedGameExecutable || selectedGameExecutable.requestId !== pendingBrowseRequest.current) {
      return;
    }
    pendingBrowseRequest.current = null;
    if (selectedGameExecutable.filePath !== null) {
      setDraft((current) => current ? { ...current, executablePath: selectedGameExecutable.filePath ?? '' } : current);
    }
  }, [selectedGameExecutable]);

  useEffect(() => {
    if (!gameSearchResults || gameSearchResults.requestId !== pendingSearchRequest) return;
    setPendingSearchRequest(null);
    setSearchResults(gameSearchResults.results);
    setSearchError(gameSearchResults.error || (gameSearchResults.results.length === 0 ? 'No games matched that search.' : null));
  }, [gameSearchResults, pendingSearchRequest]);

  useEffect(() => {
    if (!resolvedGameSearch || resolvedGameSearch.requestId !== pendingResolveRequest) return;
    setPendingResolveRequest(null);
    const resolved = resolvedGameSearch.game;
    if (resolved) {
      setDraft((current) => current ? {
        ...current,
        name: resolved.name,
        selectedGame: { ...resolved, source: 'resolver' },
      } : current);
      setSearchError(null);
    } else {
      setSearchError(resolvedGameSearch.error || 'The selected game could not be resolved.');
    }
  }, [pendingResolveRequest, resolvedGameSearch]);

  useEffect(() => {
    if (!settingsUpdateResult || settingsUpdateResult.requestId !== pendingSaveRequest) {
      return;
    }
    setPendingSaveRequest(null);
    if (settingsUpdateResult.success) {
      pendingBrowseRequest.current = null;
      setDraft(null);
      setValidationAttempted(false);
      setSubmissionError(null);
    } else {
      setSubmissionError(settingsUpdateResult.error || 'The game could not be saved.');
    }
  }, [pendingSaveRequest, settingsUpdateResult]);

  function resetForm() {
    pendingBrowseRequest.current = null;
    setValidationAttempted(false);
    setSubmissionError(null);
  }

  function startAdd() {
    resetForm();
    setSearchQuery('');
    setSearchResults([]);
    setSearchError(null);
    setDraft({ index: null, name: '', executablePath: '', selectedGame: null });
  }

  function startEdit(index: number, game: GameSetting) {
    resetForm();
    setDraft({ index, name: game.name, executablePath: game.executablePath ?? '', selectedGame: null });
  }

  function cancel() {
    resetForm();
    setPendingSaveRequest(null);
    setDraft(null);
    setPendingSearchRequest(null);
  }

  function save() {
    if (!draft) return;
    const name = draft.name.trim();
    const executablePath = draft.executablePath.trim();
    if (name === '' || !isAbsoluteExecutablePath(executablePath) || (draft.index === null && !draft.selectedGame?.gameId)) {
      setValidationAttempted(true);
      return;
    }

    const nextGame: GameSetting = draft.index === null
      ? {
          id: draft.selectedGame!.gameId!,
          name,
          executablePath,
        }
      : { ...gameList[draft.index], name, executablePath };
    const next = draft.index === null
      ? [...gameList, nextGame]
      : gameList.map((game, index) => index === draft.index ? nextGame : game);
    setPendingSaveRequest(saveGameList(next));
    setSubmissionError(null);
  }

  function browse() {
    const requestId = crypto.randomUUID();
    pendingBrowseRequest.current = requestId;
    onBrowseExecutable(requestId);
  }

  function search() {
    const query = searchQuery.trim();
    if (query === '') {
      setSearchError('Enter a game name to search.');
      return;
    }
    const requestId = crypto.randomUUID();
    setPendingSearchRequest(requestId);
    setSearchResults([]);
    setSearchError(null);
    setDraft((current) => current ? { ...current, name: '', selectedGame: null } : current);
    onSearchGames(requestId, query);
  }

  function selectResult(result: GameSearchResult) {
    if (result.gameId) {
      setDraft((current) => current ? { ...current, name: result.name, selectedGame: result } : current);
      return;
    }
    const input = result.igdbId ? `igdb:${result.igdbId}` : result.steamAppId ? `steam:${result.steamAppId}` : null;
    if (!input) return;
    const requestId = crypto.randomUUID();
    setPendingResolveRequest(requestId);
    setSearchError(null);
    onResolveGameSearch(requestId, input);
  }

  return {
    draft,
    validationAttempted,
    saving: pendingSaveRequest !== null,
    submissionError,
    searchQuery,
    searchResults,
    searchError,
    searching: pendingSearchRequest !== null,
    resolving: pendingResolveRequest !== null,
    setName: (name) => setDraft((current) => current ? { ...current, name } : current),
    setExecutablePath: (executablePath) => setDraft((current) => current ? { ...current, executablePath } : current),
    setSearchQuery,
    startAdd,
    startEdit,
    cancel,
    save,
    browse,
    search,
    selectResult,
  };
}
