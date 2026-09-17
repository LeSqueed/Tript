// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { GameSetting, GameSettings, RecordingMode } from '../settingsModel';
import type { GameAddRequestedMessage, GameInfo, GameModelStatus, GameSearchResultsMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import { Button, Toggle } from '../../components/ui/controls';
import { UnsupportedGamesList } from './game/UnsupportedGamesList';
import { IgnoredApplicationsList } from './game/IgnoredApplicationsList';
import { GameOverrideFields } from './game/GameOverrideFields';
import { CustomGameDraftEditor } from './game/CustomGameDraftEditor';
import { useCustomGameDraft } from './game/useCustomGameDraft';

const DEFAULT_CLIP_BEFORE_SECONDS = 5;
const DEFAULT_CLIP_AFTER_SECONDS = 8;
const FOCUS_HIGHLIGHT_MS = 2000;

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
  settings: GameSettings;
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
  const [highlightedGameId, setHighlightedGameId] = useState<string | null>(null);

  const gameList = Array.isArray(settings.gameList) ? settings.gameList : [];
  const ignoredApplications = Array.isArray(settings.ignoredApplications) ? settings.ignoredApplications : [];
  const builtInIds = new Set(builtInGameIds.map((id) => id.toLowerCase()));
  const saveGameList = (next: GameSetting[]) => update(page, { gameList: next });

  const draftState = useCustomGameDraft({
    gameList,
    saveGameList,
    selectedGameExecutable,
    settingsUpdateResult,
    gameSearchResults,
    resolvedGameSearch,
    onBrowseExecutable,
    onSearchGames,
    onResolveGameSearch,
  });
  const { draft } = draftState;

  useEffect(() => {
    if (!focusGameId) return;
    const row = document.querySelector(`[data-game-id="${CSS.escape(focusGameId)}"]`);
    row?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    setHighlightedGameId(focusGameId);
    onFocusHandled?.();
    const timeout = window.setTimeout(() => setHighlightedGameId(null), FOCUS_HIGHLIGHT_MS);
    return () => window.clearTimeout(timeout);
  }, [focusGameId]);

  function replaceGame(index: number, game: GameSetting) {
    saveGameList(gameList.map((candidate, i) => i === index ? game : candidate));
  }

  function patchGame(index: number, patch: Partial<GameSetting>) {
    saveGameList(gameList.map((game, i) => (i === index ? { ...game, ...patch } : game)));
  }

  function resetPackagedGame(index: number) {
    const game = gameList[index];
    replaceGame(index, { id: game.id, name: game.name });
  }

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
      <UnsupportedGamesList
        modelStatuses={modelStatuses}
        catalogueGames={catalogueGames}
        gameAddRequested={gameAddRequested}
        onRequestGame={onRequestGame}
      />
      <div className="game-list">
        <div className="game-list-heading">
          <h3 className="subheading">Games</h3>
          <Button onClick={draftState.startAdd} disabled={draft !== null}>Add custom game</Button>
        </div>
        {gameList.length === 0 ? (
          <p className="muted small">No game overrides or custom games yet; known games come from the project catalogue.</p>
        ) : (
          <p className="muted small">
            Packaged games support per-game overrides. Custom games use the exact executable path you provide.
          </p>
        )}

        {draft && (
          <CustomGameDraftEditor state={draftState} draft={draft} gameList={gameList} builtInIds={builtInIds} />
        )}

        {gameList.map((game, index) => (
          <div
            className={highlightedGameId === game.id ? 'game-row game-row-highlighted' : 'game-row'}
            key={game.id ?? index}
            data-game-id={game.id}
          >
            <div className="game-row-main">
              <strong>{game.name}</strong>
              <span className="muted small">{game.id}</span>
              {builtInIds.has(game.id.toLowerCase()) ? (
                <Button variant="ghost" onClick={() => resetPackagedGame(index)} title="Reset all overrides for this game">Reset overrides</Button>
              ) : (
                <>
                  <Button variant="ghost" onClick={() => draftState.startEdit(index, game)} disabled={draft !== null}>Edit</Button>
                  <Button
                    variant="danger"
                    onClick={() => saveGameList(gameList.filter((_, i) => i !== index))}
                    title="Remove this custom game"
                  >
                    Remove
                  </Button>
                </>
              )}
            </div>

            <div className="game-executable-readonly">
              <span className="muted small">Executable</span>
              <code>{game.executablePath ?? game.executable ?? 'Provided by the packaged game'}</code>
            </div>

            <GameOverrideFields
              game={game}
              onPatch={(patch) => patchGame(index, patch)}
              onReplace={(next) => replaceGame(index, next)}
              clipBeforeSeconds={clipBeforeSeconds}
              clipAfterSeconds={clipAfterSeconds}
              globalRecordingMode={globalRecordingMode}
              automaticClipsEnabled={automaticClipsEnabled}
            />
          </div>
        ))}
      </div>

      <IgnoredApplicationsList
        ignoredApplications={ignoredApplications}
        onRemove={(index) => update(page, {
          ignoredApplications: ignoredApplications.filter((_, i) => i !== index),
        })}
      />
    </div>
  );
}
