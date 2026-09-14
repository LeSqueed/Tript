// SPDX-License-Identifier: GPL-2.0-or-later

import { useDeferredValue, useEffect, useRef, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { DisplayCaptureMethod, GameSetting, RecordingMode } from '../settingsModel';
import type { GameAddRequestedMessage, GameInfo, GameModelStatus, GameSearchResult, GameSearchResultsMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import { Button, SelectField, TextField, Toggle } from '../../components/ui/controls';
import { useToast } from '../../components/ui/toast/ToastProvider';

function hasOverrides(game: GameSetting): boolean {
  if (game.autoRecordOverride !== null && game.autoRecordOverride !== undefined) return true;
  if (game.recordingModeOverride?.mode !== null && game.recordingModeOverride?.mode !== undefined) return true;
  if (game.captureMethodOverride?.method !== null && game.captureMethodOverride?.method !== undefined) return true;
  if (game.qualityOverride !== null && game.qualityOverride !== undefined) {
    const q = game.qualityOverride;
    if (q.resolutionWidth !== null && q.resolutionWidth !== undefined) return true;
    if (q.resolutionHeight !== null && q.resolutionHeight !== undefined) return true;
    if (q.fps !== null && q.fps !== undefined) return true;
    if (q.encoder !== null && q.encoder !== undefined) return true;
    if (q.quality !== null && q.quality !== undefined) return true;
  }
  if (game.automaticClipOverride !== null && game.automaticClipOverride !== undefined) {
    const clip = game.automaticClipOverride;
    if (clip.beforeSeconds !== null && clip.beforeSeconds !== undefined) return true;
    if (clip.afterSeconds !== null && clip.afterSeconds !== undefined) return true;
  }
  return false;
}

const CAPTURE_METHOD_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Global' },
  { value: 'Auto', label: 'Auto' },
  { value: 'Game', label: 'Game capture only' },
  { value: 'Display', label: 'Display capture only' },
];

const RECORDING_MODE_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Inherit global setting' },
  { value: 'Session', label: 'Session' },
  { value: 'SessionWithReplayBuffer', label: 'Session + Replay Buffer' },
  { value: 'ReplayBufferOnly', label: 'Replay buffer only' },
];

const AUTO_RECORD_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Inherit global setting' },
  { value: 'true', label: 'Always' },
  { value: 'false', label: 'Never' },
];

const DEFAULT_CLIP_BEFORE_SECONDS = 5;
const DEFAULT_CLIP_AFTER_SECONDS = 8;

interface CustomGameDraft {
  index: number | null;
  name: string;
  executablePath: string;
  selectedGame: GameSearchResult | null;
}

export function GamePage({
  settings,
  update,
  page,
  externalPushCount: _externalPushCount,
  builtInGameIds,
  selectedGameExecutable,
  settingsUpdateResult,
  onBrowseExecutable,
  gameSearchResults,
  onSearchGames,
  resolvedGameSearch,
  onResolveGameSearch,
  catalogueGames,
  modelStatuses,
  gameAddRequested,
  onRequestGame,
  globalClipBeforeSeconds,
  globalClipAfterSeconds,
  globalRecordingMode,
  automaticClipsEnabled,
  focusGameId,
  onFocusHandled,
}: {
  settings: import('../settingsModel').GameSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  externalPushCount: number;
  builtInGameIds: readonly string[];
  selectedGameExecutable: SelectedGameExecutableMessage | null;
  settingsUpdateResult: SettingsUpdateResultMessage | null;
  onBrowseExecutable: (requestId: string) => void;
  gameSearchResults: GameSearchResultsMessage | null;
  onSearchGames: (requestId: string, query: string) => void;
  resolvedGameSearch: ResolvedGameSearchMessage | null;
  onResolveGameSearch: (requestId: string, input: string) => void;
  catalogueGames: GameInfo[];
  modelStatuses: GameModelStatus[];
  gameAddRequested: GameAddRequestedMessage | null;
  onRequestGame: (requestId: string, gameId: string) => void;
  globalClipBeforeSeconds?: number;
  globalClipAfterSeconds?: number;
  globalRecordingMode: RecordingMode;
  automaticClipsEnabled: boolean;
  focusGameId?: string | null;
  onFocusHandled?: () => void;
}) {
  const clipBeforeSeconds = globalClipBeforeSeconds ?? DEFAULT_CLIP_BEFORE_SECONDS;
  const clipAfterSeconds = globalClipAfterSeconds ?? DEFAULT_CLIP_AFTER_SECONDS;
  const [draft, setDraft] = useState<CustomGameDraft | null>(null);
  const [validationAttempted, setValidationAttempted] = useState(false);
  const [pendingSaveRequest, setPendingSaveRequest] = useState<string | null>(null);
  const [submissionError, setSubmissionError] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [searchResults, setSearchResults] = useState<GameSearchResult[]>([]);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [pendingSearchRequest, setPendingSearchRequest] = useState<string | null>(null);
  const [pendingResolveRequest, setPendingResolveRequest] = useState<string | null>(null);
  const [ignoredApplicationFilter, setIgnoredApplicationFilter] = useState('');
  const deferredIgnoredApplicationFilter = useDeferredValue(ignoredApplicationFilter.trim().toLowerCase());
  const pendingBrowseRequest = useRef<string | null>(null);
  const [gameRequestState, setGameRequestState] = useState<Record<string, 'pending' | 'accepted' | 'alreadyRequested' | 'rateLimited'>>({});
  const pendingGameRequests = useRef<Map<string, string>>(new Map());
  const toast = useToast();
  const [highlightedGameId, setHighlightedGameId] = useState<string | null>(null);

  useEffect(() => {
    if (!focusGameId) return;
    const row = document.querySelector(`[data-game-id="${CSS.escape(focusGameId)}"]`);
    row?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    setHighlightedGameId(focusGameId);
    onFocusHandled?.();
    const timeout = window.setTimeout(() => setHighlightedGameId(null), 2000);
    return () => window.clearTimeout(timeout);
  }, [focusGameId]);

  const gameList = Array.isArray(settings.gameList) ? settings.gameList : [];
  const ignoredApplications = Array.isArray(settings.ignoredApplications) ? settings.ignoredApplications : [];
  const visibleIgnoredApplications = ignoredApplications
    .map((executablePath, index) => ({ executablePath, index }))
    .filter(({ executablePath }) => executablePath.toLowerCase().includes(deferredIgnoredApplicationFilter));
  const builtInIds = new Set(builtInGameIds.map((id) => id.toLowerCase()));

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
    if (resolvedGameSearch.game) {
      setDraft((current) => current ? {
        ...current,
        name: resolvedGameSearch.game!.name,
        selectedGame: { ...resolvedGameSearch.game!, source: 'resolver' },
      } : current);
      setSearchError(null);
    } else {
      setSearchError(resolvedGameSearch.error || 'The selected game could not be resolved.');
    }
  }, [pendingResolveRequest, resolvedGameSearch]);

  useEffect(() => {
    if (!gameAddRequested) return;
    const gameId = pendingGameRequests.current.get(gameAddRequested.requestId);
    if (!gameId) return;
    pendingGameRequests.current.delete(gameAddRequested.requestId);

    if (gameAddRequested.status === 'rejected') {
      setGameRequestState((current) => {
        const { [gameId]: _dropped, ...rest } = current;
        return rest;
      });
      toast.push({ kind: 'error', message: gameAddRequested.error ?? 'The request could not be sent.' });
      return;
    }

    setGameRequestState((current) => ({ ...current, [gameId]: gameAddRequested.status as 'accepted' | 'alreadyRequested' | 'rateLimited' }));
    if (gameAddRequested.status === 'accepted') {
      toast.push({ kind: 'success', message: 'Request received — thanks!' });
    } else if (gameAddRequested.status === 'alreadyRequested') {
      toast.push({ kind: 'info', message: "You've already requested this game." });
    } else if (gameAddRequested.status === 'rateLimited') {
      const hours = gameAddRequested.retryAfterSeconds
        ? Math.max(1, Math.round(gameAddRequested.retryAfterSeconds / 3600))
        : 6;
      toast.push({ kind: 'warning', message: `Too many requests — try again in about ${hours} hour${hours === 1 ? '' : 's'}.` });
    }
  }, [gameAddRequested, toast]);

  function requestGame(gameId: string) {
    const requestId = crypto.randomUUID();
    pendingGameRequests.current.set(requestId, gameId);
    setGameRequestState((current) => ({ ...current, [gameId]: 'pending' }));
    onRequestGame(requestId, gameId);
  }

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

  function removeGame(index: number) {
    const next = gameList.filter((_, i) => i !== index);
    update(page, { gameList: next });
  }

  function removeIgnoredApplication(index: number) {
    update(page, { ignoredApplications: ignoredApplications.filter((_, i) => i !== index) });
  }

  function patchGame(index: number, patch: Partial<GameSetting>) {
    const next = gameList.map((game, i) => (i === index ? { ...game, ...patch } : game));
    update(page, { gameList: next });
  }

  function resetPackagedGame(index: number) {
    const game = gameList[index];
    const reset: GameSetting = {
      id: game.id,
      name: game.name,
    };
    update(page, { gameList: gameList.map((candidate, i) => i === index ? reset : candidate) });
  }

  function replaceGame(index: number, game: GameSetting) {
    update(page, { gameList: gameList.map((candidate, i) => i === index ? game : candidate) });
  }

  function patchAutomaticClipOverride(
    index: number,
    game: GameSetting,
    before: number | null,
    after: number | null,
  ) {
    const effectiveBefore = before ?? clipBeforeSeconds;
    const effectiveAfter = after ?? clipAfterSeconds;
    const nextAfter = effectiveAfter < effectiveBefore ? effectiveBefore : after;
    if (before === null && nextAfter === null) {
      const { automaticClipOverride: _dropped, ...rest } = game;
      replaceGame(index, rest as GameSetting);
    } else {
      patchGame(index, {
        automaticClipOverride: { beforeSeconds: before, afterSeconds: nextAfter },
      });
    }
  }

  function saveDraft() {
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
    setPendingSaveRequest(update(page, { gameList: next }));
    setSubmissionError(null);
  }

  function browseForExecutable() {
    const requestId = crypto.randomUUID();
    pendingBrowseRequest.current = requestId;
    onBrowseExecutable(requestId);
  }

  function searchGames() {
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

  function selectSearchResult(result: GameSearchResult) {
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

  const unsupportedGames = modelStatuses.filter((status) => status.stage === 'unsupported');

  return (
    <div className="settings-page" data-page="game">
      <section className="settings-section" aria-labelledby="game-auto-capture-heading">
        <h3 className="subheading" id="game-auto-capture-heading">Automatic capture</h3>
        <Toggle
          checked={settings.autoRecordDetectedGames !== false}
          onChange={(checked) => update(page, { autoRecordDetectedGames: checked })}
          label="Automatically record recognized games when they launch"
        />
      </section>
      {unsupportedGames.length > 0 && (
        <div className="game-list unsupported-games" data-testid="unsupported-games">
          <div className="game-list-heading">
            <h3 className="subheading">Unsupported games</h3>
          </div>
          <p className="muted small">
            These games don't have a detection model yet. You can ask for one to be added.
          </p>
          {unsupportedGames.map((status) => {
            const name = catalogueGames.find((game) => game.id === status.gameId)?.name ?? status.gameId;
            const state = gameRequestState[status.gameId];
            const label = state === 'pending' ? 'Requesting…'
              : state === 'accepted' || state === 'alreadyRequested' ? 'Requested'
                : state === 'rateLimited' ? 'Request this game'
                  : 'Request this game';
            return (
              <div className="game-row" key={status.gameId} data-testid="unsupported-game-row">
                <div className="game-row-main">
                  <strong>{name}</strong>
                  <span className="muted small">{status.gameId}</span>
                  <Button
                    variant="ghost"
                    onClick={() => requestGame(status.gameId)}
                    disabled={state === 'pending' || state === 'accepted' || state === 'alreadyRequested'}
                  >
                    {label}
                  </Button>
                </div>
              </div>
            );
          })}
        </div>
      )}
      <div className="game-list">
        <div className="game-list-heading">
          <h3 className="subheading">Games</h3>
          <Button onClick={() => {
            pendingBrowseRequest.current = null;
            setValidationAttempted(false);
            setSubmissionError(null);
            setSearchQuery('');
            setSearchResults([]);
            setSearchError(null);
            setDraft({ index: null, name: '', executablePath: '', selectedGame: null });
          }} disabled={draft !== null}>Add custom game</Button>
        </div>
        {gameList.length === 0 ? (
          <p className="muted small">No game overrides or custom games yet; known games come from the project catalogue.</p>
        ) : (
          <p className="muted small">
            Packaged games support per-game overrides. Custom games use the exact executable path you provide.
          </p>
        )}

        {draft && (
          <div className="game-row game-draft" data-testid="custom-game-draft">
            <h4 className="game-row-title">{draft.index === null ? 'Add custom game' : 'Edit custom game'}</h4>
            <div className="game-draft-fields">
              {draft.index === null ? <div className="field game-search-field">
                <label className="field-label" htmlFor="custom-game-search">Find game</label>
                <div className="game-executable-field">
                  <TextField
                    id="custom-game-search"
                    value={searchQuery}
                    onChange={setSearchQuery}
                    placeholder="Search by game title"
                    onKeyDown={(event) => { if (event.key === 'Enter') searchGames(); }}
                  />
                  <Button variant="ghost" onClick={searchGames} disabled={pendingSearchRequest !== null}>
                    {pendingSearchRequest ? 'Searching…' : 'Search'}
                  </Button>
                </div>
                {searchError && <p className="game-validation" role="alert">{searchError}</p>}
                {searchResults.length > 0 && <div className="game-search-results" role="listbox" aria-label="Game search results">
                  {searchResults.map((result, index) => {
                    const id = result.gameId?.trim();
                    const duplicate = id ? gameList.some((game) => game.id.toLowerCase() === id.toLowerCase()) || builtInIds.has(id.toLowerCase()) : false;
                    const resolvable = Boolean(id || result.igdbId || result.steamAppId);
                    const disabled = !resolvable || duplicate || pendingResolveRequest !== null;
                    return <button
                      type="button"
                      role="option"
                      aria-selected={draft.selectedGame === result}
                      className={draft.selectedGame === result ? 'game-search-result selected' : 'game-search-result'}
                      disabled={disabled}
                      key={`${id ?? result.source}-${result.name}-${index}`}
                      onClick={() => selectSearchResult(result)}
                    >
                      <strong>{result.name}{result.year ? ` (${result.year})` : ''}</strong>
                      <span>{[result.platforms, result.source, duplicate ? 'Already configured' : !resolvable ? 'Cannot resolve' : null].filter(Boolean).join(' · ')}</span>
                    </button>;
                  })}
                </div>}
              </div> : <div className="field">
                <label className="field-label" htmlFor="custom-game-name">Game name</label>
                <TextField
                  id="custom-game-name"
                  value={draft.name}
                  onChange={(name) => setDraft({ ...draft, name })}
                  aria-invalid={validationAttempted && draft.name.trim() === ''}
                  aria-describedby={validationAttempted && draft.name.trim() === '' ? 'custom-game-name-error' : submissionError ? 'custom-game-save-error' : undefined}
                />
              </div>}
              <div className="field">
                <label className="field-label" htmlFor="custom-game-executable">Executable path</label>
                <div className="game-executable-field">
                  <TextField
                    id="custom-game-executable"
                    value={draft.executablePath}
                    onChange={(executablePath) => setDraft({ ...draft, executablePath })}
                    placeholder="C:\\Games\\Example\\game.exe"
                    aria-invalid={validationAttempted && !isAbsoluteExecutablePath(draft.executablePath.trim())}
                    aria-describedby={validationAttempted && !isAbsoluteExecutablePath(draft.executablePath.trim()) ? 'custom-game-executable-error' : submissionError ? 'custom-game-save-error' : undefined}
                  />
                  <Button variant="ghost" onClick={browseForExecutable}>Browse</Button>
                </div>
              </div>
            </div>
            {validationAttempted && draft.name.trim() === '' && <p id="custom-game-name-error" className="game-validation" role="alert">Enter a game name.</p>}
            {validationAttempted && draft.index === null && !draft.selectedGame?.gameId && <p className="game-validation" role="alert">Select a game from the search results.</p>}
            {validationAttempted && !isAbsoluteExecutablePath(draft.executablePath.trim()) && <p id="custom-game-executable-error" className="game-validation" role="alert">Enter the exact executable path.</p>}
            {submissionError && <p id="custom-game-save-error" className="game-validation" role="alert">{submissionError}</p>}
            <div className="game-draft-actions">
              <Button onClick={saveDraft} disabled={pendingSaveRequest !== null}>Save</Button>
              <Button variant="ghost" onClick={() => {
                pendingBrowseRequest.current = null;
                setPendingSaveRequest(null);
                setDraft(null);
                setValidationAttempted(false);
                setSubmissionError(null);
                setPendingSearchRequest(null);
              }}>Cancel</Button>
            </div>
          </div>
        )}

        {gameList.map((game, index) => {
          const packaged = builtInIds.has(game.id.toLowerCase());
          const modified = hasOverrides(game);
          const effectiveMode = game.recordingModeOverride?.mode ?? globalRecordingMode;
          const automaticClipOverridesDisabled = !automaticClipsEnabled
            || (effectiveMode !== 'SessionWithReplayBuffer' && effectiveMode !== 'ReplayBufferOnly');
          const automaticClipDisabledReason = !automaticClipsEnabled
            ? 'Automatic highlights are off in global settings.'
            : 'Automatic highlights need a replay buffer recording mode.';
          return (
          <div
            className={highlightedGameId === game.id ? 'game-row game-row-highlighted' : 'game-row'}
            key={game.id ?? index}
            data-game-id={game.id}
          >
            <div className="game-row-main">
              <strong>{game.name}</strong>
              <span className="muted small">{game.id}</span>
              {packaged ? (
                <Button variant="ghost" onClick={() => resetPackagedGame(index)} title="Reset all overrides for this game">Reset overrides</Button>
              ) : (
                <>
                  <Button variant="ghost" onClick={() => {
                    pendingBrowseRequest.current = null;
                    setValidationAttempted(false);
                    setSubmissionError(null);
                    setDraft({ index, name: game.name, executablePath: game.executablePath ?? '', selectedGame: null });
                  }} disabled={draft !== null}>Edit</Button>
                  <Button variant="danger" onClick={() => removeGame(index)} title="Remove this custom game">Remove</Button>
                </>
              )}
            </div>

            <div className="game-executable-readonly">
              <span className="muted small">Executable</span>
              <code>{game.executablePath ?? game.executable ?? 'Provided by the packaged game'}</code>
            </div>

            <details className="settings-advanced">
              <summary>Customize for this game{modified ? <span className="settings-advanced-marker">modified</span> : null}</summary>
              <div className="settings-advanced-body game-row-overrides">
              <label className="settings-inline-field">
                <span className="muted small">Recording mode</span>
                <SelectField
                  value={game.recordingModeOverride?.mode ?? ''}
                  options={RECORDING_MODE_OVERRIDES}
                  onChange={(value) => {
                    if (value === '') {
                      const { recordingModeOverride: _dropped, ...rest } = game;
                      replaceGame(index, rest as GameSetting);
                    } else {
                      patchGame(index, { recordingModeOverride: { mode: value as RecordingMode } });
                    }
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Auto-record on launch</span>
                <SelectField
                  value={game.autoRecordOverride === true ? 'true' : game.autoRecordOverride === false ? 'false' : ''}
                  options={AUTO_RECORD_OVERRIDES}
                  onChange={(value) => {
                    patchGame(index, { autoRecordOverride: value === '' ? null : value === 'true' });
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Capture</span>
                <SelectField
                  value={game.captureMethodOverride?.method ?? ''}
                  options={CAPTURE_METHOD_OVERRIDES}
                  onChange={(value) => {
                    if (value === '') {
                      const { captureMethodOverride: _dropped, ...rest } = game;
                      replaceGame(index, rest as GameSetting);
                    } else {
                      patchGame(index, {
                        captureMethodOverride: { method: value as DisplayCaptureMethod },
                      });
                    }
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">FPS</span>
                <TextField
                  type="number"
                  min={1}
                  value={game.qualityOverride?.fps ?? ''}
                  placeholder="global"
                  onChange={(value) => {
                    patchGame(index, {
                      qualityOverride: {
                        ...(game.qualityOverride ?? {}),
                        fps: value === '' ? null : Number(value),
                      },
                    });
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Encoder</span>
                <TextField
                  type="text"
                  value={game.qualityOverride?.encoder ?? ''}
                  placeholder="global"
                  onChange={(value) => {
                    patchGame(index, {
                      qualityOverride: {
                        ...(game.qualityOverride ?? {}),
                        encoder: value === '' ? null : value,
                      },
                    });
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Quality</span>
                <TextField
                  type="number"
                  min={1}
                  value={game.qualityOverride?.quality ?? ''}
                  placeholder="global"
                  onChange={(value) => {
                    patchGame(index, {
                      qualityOverride: {
                        ...(game.qualityOverride ?? {}),
                        quality: value === '' ? null : Number(value),
                      },
                    });
                  }}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Before (s)</span>
                <TextField
                  type="number"
                  min={1}
                  disabled={automaticClipOverridesDisabled}
                  title={automaticClipOverridesDisabled ? automaticClipDisabledReason : undefined}
                  value={game.automaticClipOverride?.beforeSeconds ?? ''}
                  placeholder="global"
                  onChange={(value) => patchAutomaticClipOverride(
                    index,
                    game,
                    value === '' ? null : Number(value),
                    game.automaticClipOverride?.afterSeconds ?? null,
                  )}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">After (s)</span>
                <TextField
                  type="number"
                  min={1}
                  disabled={automaticClipOverridesDisabled}
                  title={automaticClipOverridesDisabled ? automaticClipDisabledReason : undefined}
                  value={game.automaticClipOverride?.afterSeconds ?? ''}
                  placeholder="global"
                  onChange={(value) => patchAutomaticClipOverride(
                    index,
                    game,
                    game.automaticClipOverride?.beforeSeconds ?? null,
                    value === '' ? null : Number(value),
                  )}
                />
              </label>
              </div>
            </details>
          </div>
          );
        })}
      </div>

      <details className="settings-advanced ignored-applications">
        <summary>
          Ignored applications
          <span className="settings-advanced-marker">{ignoredApplications.length}</span>
        </summary>
        <div className="settings-advanced-body">
          <p className="muted small">These applications will not be suggested as custom games.</p>
          {ignoredApplications.length === 0 ? (
            <p className="muted small">No ignored applications.</p>
          ) : (
            <>
              <label className="field ignored-application-filter">
                <span className="field-label">Filter ignored applications</span>
                <TextField
                  value={ignoredApplicationFilter}
                  onChange={setIgnoredApplicationFilter}
                  placeholder="Name or executable path"
                />
              </label>
              {visibleIgnoredApplications.length === 0 ? (
                <p className="muted small">No ignored applications match this filter.</p>
              ) : (
                <div className="ignored-application-list">
                  {visibleIgnoredApplications.map(({ executablePath, index }) => (
                    <div className="ignored-application-row" key={`${executablePath}:${index}`}>
                      <div className="ignored-application-details">
                        <strong>{applicationName(executablePath)}</strong>
                        <code>{executablePath}</code>
                      </div>
                      <Button
                        variant="ghost"
                        onClick={() => removeIgnoredApplication(index)}
                        aria-label={`Remove ${applicationName(executablePath)} from ignored applications`}
                      >
                        Remove
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      </details>
    </div>
  );
}

function isAbsoluteExecutablePath(path: string): boolean {
  return /^(?:[a-zA-Z]:[\\/]|\\\\|\/).+[^\\/]$/.test(path);
}

function applicationName(path: string): string {
  return path.split(/[\\/]/).filter(Boolean).at(-1) || path;
}
