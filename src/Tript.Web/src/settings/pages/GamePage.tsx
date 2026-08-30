// SPDX-License-Identifier: GPL-2.0-or-later
//
// The game page: the known games and their per-game overrides. How capture works is a Capture-page
// setting; this page only says which games depart from it. The game-capture timeout is the soft
// timeout: how long game capture waits for the game's window before falling back.

import { useEffect, useRef, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { DisplayCaptureMethod, GameSetting, RecordingMode } from '../settingsModel';
import type { SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import { Button, Checkbox, Field, SelectField, TextField } from '../../components/ui/controls';

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
];

interface CustomGameDraft {
  index: number | null;
  name: string;
  executablePath: string;
}

export function GamePage({
  settings,
  update,
  page,
  externalPushCount,
  builtInGameIds,
  selectedGameExecutable,
  settingsUpdateResult,
  onBrowseExecutable,
}: {
  settings: import('../settingsModel').GameSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  externalPushCount: number;
  builtInGameIds: readonly string[];
  selectedGameExecutable: SelectedGameExecutableMessage | null;
  settingsUpdateResult: SettingsUpdateResultMessage | null;
  onBrowseExecutable: (requestId: string) => void;
}) {
  const [timeoutSeconds, setTimeoutSeconds] = useState<string>(String(settings.gameCaptureTimeout));
  const [draft, setDraft] = useState<CustomGameDraft | null>(null);
  const [validationAttempted, setValidationAttempted] = useState(false);
  const [pendingSaveRequest, setPendingSaveRequest] = useState<string | null>(null);
  const [submissionError, setSubmissionError] = useState<string | null>(null);
  const pendingBrowseRequest = useRef<string | null>(null);

  // Re-sync the timeout draft from the model only on an external push, never on our own echo.
  useEffect(() => {
    setTimeoutSeconds(String(settings.gameCaptureTimeout));
  }, [externalPushCount, settings.gameCaptureTimeout]);

  const gameList = Array.isArray(settings.gameList) ? settings.gameList : [];
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

  function patchGame(index: number, patch: Partial<GameSetting>) {
    const next = gameList.map((game, i) => (i === index ? { ...game, ...patch } : game));
    update(page, { gameList: next });
  }

  function resetPackagedGame(index: number) {
    const game = gameList[index];
    const reset: GameSetting = {
      id: game.id,
      name: game.name,
      integrations: { enabled: false },
    };
    update(page, { gameList: gameList.map((candidate, i) => i === index ? reset : candidate) });
  }

  function replaceGame(index: number, game: GameSetting) {
    update(page, { gameList: gameList.map((candidate, i) => i === index ? game : candidate) });
  }

  function saveDraft() {
    if (!draft) return;
    const name = draft.name.trim();
    const executablePath = draft.executablePath.trim();
    if (name === '' || !isAbsoluteExecutablePath(executablePath)) {
      setValidationAttempted(true);
      return;
    }

    const nextGame: GameSetting = draft.index === null
      ? {
          id: `custom-${crypto.randomUUID()}`,
          name,
          executablePath,
          integrations: { enabled: false },
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

  function commitTimeout() {
    const parsed = Number(timeoutSeconds);
    if (Number.isFinite(parsed) && parsed > 0) {
      update(page, { gameCaptureTimeout: Math.round(parsed) });
    } else {
      setTimeoutSeconds(String(settings.gameCaptureTimeout));
    }
  }

  return (
    <div className="settings-page" data-page="game">
      <Field label="Game-capture timeout" hint="How long game capture waits for the game's window before falling back, in seconds.">
        <TextField
                  type="number"
          min={1}
          value={timeoutSeconds}
          onChange={(value) => setTimeoutSeconds(value)}
          onBlur={commitTimeout}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitTimeout();
            }
          }}
          aria-label="Game-capture timeout"
        />
      </Field>

      <div className="game-list">
        <div className="game-list-heading">
          <h3 className="subheading">Games</h3>
          <Button onClick={() => {
            pendingBrowseRequest.current = null;
            setValidationAttempted(false);
            setSubmissionError(null);
            setDraft({ index: null, name: '', executablePath: '' });
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
              <div className="field">
                <label className="field-label" htmlFor="custom-game-name">Game name</label>
                <TextField
                  id="custom-game-name"
                  value={draft.name}
                  onChange={(name) => setDraft({ ...draft, name })}
                  aria-invalid={validationAttempted && draft.name.trim() === ''}
                  aria-describedby={validationAttempted && draft.name.trim() === '' ? 'custom-game-name-error' : submissionError ? 'custom-game-save-error' : undefined}
                />
              </div>
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
              }}>Cancel</Button>
            </div>
          </div>
        )}

        {gameList.map((game, index) => {
          const packaged = builtInIds.has(game.id.toLowerCase());
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
                    setDraft({ index, name: game.name, executablePath: game.executablePath ?? '' });
                  }} disabled={draft !== null}>Edit</Button>
                  <Button variant="danger" onClick={() => removeGame(index)} title="Remove this custom game">Remove</Button>
                </>
              )}
            </div>

            <div className="game-executable-readonly">
              <span className="muted small">Executable</span>
              <code>{game.executablePath ?? game.executable ?? 'Provided by the packaged game'}</code>
            </div>

            <div className="game-row-overrides">
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
                <span className="muted small">Integrations</span>
                <Checkbox
                  checked={game.integrations?.enabled ?? false}
                  onChange={(enabled) => patchGame(index, { integrations: { enabled } })}
                />
              </label>
            </div>
          </div>
          );
        })}
      </div>
    </div>
  );
}

function isAbsoluteExecutablePath(path: string): boolean {
  return /^(?:[a-zA-Z]:[\\/]|\\\\|\/).+[^\\/]$/.test(path);
}
