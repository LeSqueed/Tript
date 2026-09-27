// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { GameSetting, GameSettings, RecordingMode } from '../settingsModel';
import type { GameAddRequestedMessage, GameModelStatus, GameSearchResultsMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import { Button, Toggle } from '../../components/ui/controls';
import { GameSupportAction, GameSupportTag, useGameRequests } from './game/GameSupport';
import { IgnoredApplicationsList } from './game/IgnoredApplicationsList';
import { GameOverrideFields, hasOverrides } from './game/GameOverrideFields';
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
  selectedGameExecutable,
  settingsUpdateResult,
  onBrowseExecutable,
  gameSearchResults,
  onSearchGames,
  resolvedGameSearch,
  onResolveGameSearch,
  modelStatuses,
  gameAddRequested,
  onRequestGame,
  onDownloadModel,
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
  selectedGameExecutable: SelectedGameExecutableMessage | null;
  settingsUpdateResult: SettingsUpdateResultMessage | null;
  onBrowseExecutable: (requestId: string) => void;
  gameSearchResults: GameSearchResultsMessage | null;
  onSearchGames: (requestId: string, query: string) => void;
  resolvedGameSearch: ResolvedGameSearchMessage | null;
  onResolveGameSearch: (requestId: string, input: string) => void;
  modelStatuses: GameModelStatus[];
  gameAddRequested: GameAddRequestedMessage | null;
  onRequestGame: (requestId: string, gameId: string) => void;
  onDownloadModel: (gameId: string) => void;
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
  const gameRequests = useGameRequests(gameAddRequested, onRequestGame);
  const statusFor = (gameId: string) =>
    modelStatuses.find((status) => status.gameId.toLowerCase() === gameId.toLowerCase());

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

  function resetOverrides(index: number) {
    const {
      autoRecordOverride: _autoRecord,
      recordingModeOverride: _recordingMode,
      qualityOverride: _quality,
      captureMethodOverride: _captureMethod,
      automaticClipOverride: _automaticClip,
      ...rest
    } = gameList[index];
    replaceGame(index, rest);
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
      <div className="game-list">
        <div className="game-list-heading">
          <h3 className="subheading">Games</h3>
          <Button onClick={draftState.startAdd} disabled={draft !== null}>Add custom game</Button>
        </div>
        {gameList.length === 0 ? (
          <p className="muted small">No games yet. Games are added when Tript recognizes them, or add one yourself.</p>
        ) : (
          <p className="muted small">Each game uses the exact executable path shown and supports per-game overrides.</p>
        )}

        {draft && (
          <CustomGameDraftEditor state={draftState} draft={draft} gameList={gameList} />
        )}

        {gameList.map((game, index) => (
          <div
            className={highlightedGameId === game.id ? 'game-row game-row-highlighted' : 'game-row'}
            key={game.id ?? index}
            data-game-id={game.id}
          >
            <div className="game-row-main">
              <div className="game-row-identity">
                <div className="game-row-name">
                  <strong>{game.name}</strong>
                  <GameSupportTag status={statusFor(game.id)} />
                </div>
                <span className="muted small">{game.id}</span>
              </div>
              <div className="game-row-actions">
                <GameSupportAction
                  gameId={game.id}
                  status={statusFor(game.id)}
                  requests={gameRequests}
                  onDownloadModel={onDownloadModel}
                />
                {hasOverrides(game) && (
                  <Button variant="ghost" onClick={() => resetOverrides(index)} title="Reset all overrides for this game">Reset overrides</Button>
                )}
                <Button variant="ghost" onClick={() => draftState.startEdit(index, game)} disabled={draft !== null}>Edit</Button>
                <Button
                  variant="danger"
                  onClick={() => saveGameList(gameList.filter((_, i) => i !== index))}
                  title="Remove this game"
                >
                  Remove
                </Button>
              </div>
            </div>

            <div className="game-executable-readonly">
              <span className="muted small">Executable</span>
              <code>{game.executablePath ?? 'Detected on first launch'}</code>
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
