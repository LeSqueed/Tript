// SPDX-License-Identifier: GPL-2.0-or-later
//
// The game page: the known games and their per-game overrides. How capture works is a Capture-page
// setting (including the game-capture timeout); this page only says which games depart from it.
// Per-game overrides are collapsed behind a disclosure per row, marked "modified" when set.

import { useDeferredValue, useEffect, useRef, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { DisplayCaptureMethod, GameSetting, RecordingMode } from '../settingsModel';
import type { GameSearchResult, GameSearchResultsMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import { Button, SelectField, TextField } from '../../components/ui/controls';

/** True when a game departs from the global settings in any way its row exposes. */
function hasOverrides(game: GameSetting): boolean {
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

// "" means inherit; the global lives on the Capture page.
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

/** The default highlight window the backend applies when a settings push carries no value. */
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
  // Accepted for the shared page contract; this page keeps no push-resynced draft.
  externalPushCount: _externalPushCount,
  builtInGameIds,
  selectedGameExecutable,
  settingsUpdateResult,
  onBrowseExecutable,
  gameSearchResults,
  onSearchGames,
  resolvedGameSearch,
  onResolveGameSearch,
  globalClipBeforeSeconds,
  globalClipAfterSeconds,
  globalRecordingMode,
  automaticClipsEnabled,
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
  /**
   * The recording page's automatic-clip window. Absent means an older backend push carried no
   * value and the backend default is the inherit baseline.
   */
  globalClipBeforeSeconds?: number;
  globalClipAfterSeconds?: number;
  globalRecordingMode: RecordingMode;
  automaticClipsEnabled: boolean;
}) {
  // The inherit baseline for each side: the global recording-page value, or the backend default.
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

  /**
   * Commit a per-game automatic-clip override side (null means inherit the global side). The
   * effective pair is `override ?? global` for each side, and it must stay coherent: the effective
   * after can never fall below the effective before, or the backend rejects the patch (task 10).
   * When a new before outruns the effective after, after is raised to it in the same patch; a typed
   * after below the effective before is clamped up. When both sides inherit, the override object is
   * dropped entirely.
   */
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

  return (
    <div className="settings-page" data-page="game">
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
          <div className="game-row" key={game.id ?? index}>
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
