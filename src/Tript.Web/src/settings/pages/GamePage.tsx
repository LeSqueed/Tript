// SPDX-License-Identifier: GPL-2.0-or-later
//
// The game page: the capture-mode behaviour (GameOnly is our own prior work and part of first
// light) and the list of known games with their per-game overrides. The game-capture timeout is the
// soft timeout: how long game capture waits for the game's window before falling back.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { GameCaptureMode, GameSetting, RecordingMode } from '../settingsModel';
import { Button, Checkbox, Field, SelectField, TextField } from '../../components/ui/controls';
import { executablePatch } from '../gameExecutable';

const CAPTURE_MODES: { value: GameCaptureMode; label: string }[] = [
  { value: 'Auto', label: 'Auto — detect and attach to the game automatically' },
  { value: 'GameOnly', label: 'GameOnly — game capture on the detected game process' },
];

const RECORDING_MODE_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Inherit global setting' },
  { value: 'Session', label: 'Session' },
  { value: 'Buffer', label: 'Buffer' },
  { value: 'Hybrid', label: 'Hybrid' },
];

export function GamePage({
  settings,
  update,
  page,
  externalPushCount,
}: {
  settings: import('../settingsModel').GameSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  externalPushCount: number;
}) {
  const [timeoutSeconds, setTimeoutSeconds] = useState<string>(String(settings.gameCaptureTimeout));

  // Re-sync the timeout draft from the model only on an external push, never on our own echo.
  useEffect(() => {
    setTimeoutSeconds(String(settings.gameCaptureTimeout));
  }, [externalPushCount, settings.gameCaptureTimeout]);

  const [newGameName, setNewGameName] = useState('');
  const [newGameId, setNewGameId] = useState('');

  const gameList = Array.isArray(settings.gameList) ? settings.gameList : [];

  function addGame() {
    const name = newGameName.trim();
    const id = newGameId.trim() || name;
    if (!name || !id) {
      return;
    }
    const next: GameSetting[] = [
      ...gameList,
      {
        id,
        name,
        integrations: { enabled: false },
      },
    ];
    update(page, { gameList: next });
    setNewGameName('');
    setNewGameId('');
  }

  function removeGame(index: number) {
    const next = gameList.filter((_, i) => i !== index);
    update(page, { gameList: next });
  }

  function patchGame(index: number, patch: Partial<GameSetting>) {
    const next = gameList.map((game, i) => (i === index ? { ...game, ...patch } : game));
    update(page, { gameList: next });
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
      <Field label="Capture mode" hint="GameOnly is part of first light — game capture on the detected process.">
        <SelectField
          value={settings.captureMode}
          onChange={(value) => update(page, { captureMode: value as GameCaptureMode })}
          options={CAPTURE_MODES}
        />
      </Field>

      <Field label="Game-capture timeout" hint="How long game capture waits for the game's window before falling back, in seconds.">
        <input
          type="number"
          className="input"
          min={1}
          value={timeoutSeconds}
          onChange={(event) => setTimeoutSeconds(event.target.value)}
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
        <h3 className="settings-subheading">Known games</h3>
        {gameList.length === 0 ? (
          <p className="muted small">No games yet — add one to set per-game overrides.</p>
        ) : (
          <p className="muted small">
            Executable is the process name the recorder watches for and attaches game capture to. It
            is often not the display name — Counter-Strike 2 runs as <code>cs2</code>. Leave it blank
            to watch for the name itself.
          </p>
        )}

        {gameList.map((game, index) => (
          <div className="game-row" key={game.id ?? index}>
            <div className="game-row-main">
              <TextField value={game.name} onChange={(value) => patchGame(index, { name: value })} />
              <span className="muted small">{game.id}</span>
              <Button variant="danger" onClick={() => removeGame(index)} title="Remove this game">
                Remove
              </Button>
            </div>

            <div className="game-row-overrides">
              <label className="settings-inline-field">
                <span className="muted small">Executable</span>
                <input
                  type="text"
                  className="input"
                  value={game.executable ?? ''}
                  // The name itself, so the field shows exactly what blank falls back to.
                  placeholder={game.name}
                  aria-label={`Executable for ${game.name}`}
                  onChange={(event) => patchGame(index, executablePatch(event.target.value))}
                />
              </label>

              <label className="settings-inline-field">
                <span className="muted small">Recording mode</span>
                <select
                  className="input select"
                  value={game.recordingModeOverride?.mode ?? ''}
                  onChange={(event) => {
                    const value = event.target.value;
                    if (value === '') {
                      const { recordingModeOverride: _dropped, ...rest } = game;
                      patchGame(index, rest);
                    } else {
                      patchGame(index, { recordingModeOverride: { mode: value as RecordingMode } });
                    }
                  }}
                >
                  {RECORDING_MODE_OVERRIDES.map((option) => (
                    <option key={option.value} value={option.value}>
                      {option.label}
                    </option>
                  ))}
                </select>
              </label>

              <label className="settings-inline-field">
                <span className="muted small">FPS</span>
                <input
                  type="number"
                  className="input"
                  min={1}
                  value={game.qualityOverride?.fps ?? ''}
                  placeholder="global"
                  onChange={(event) => {
                    const value = event.target.value;
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
                <input
                  type="text"
                  className="input"
                  value={game.qualityOverride?.encoder ?? ''}
                  placeholder="global"
                  onChange={(event) => {
                    const value = event.target.value;
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
                <input
                  type="number"
                  className="input"
                  min={1}
                  value={game.qualityOverride?.quality ?? ''}
                  placeholder="global"
                  onChange={(event) => {
                    const value = event.target.value;
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
        ))}

        <div className="game-add">
          <TextField value={newGameName} onChange={setNewGameName} placeholder="Game name" />
          <TextField value={newGameId} onChange={setNewGameId} placeholder="id (defaults to name)" />
          <Button onClick={addGame} disabled={!newGameName.trim()}>
            Add game
          </Button>
        </div>
      </div>
    </div>
  );
}
