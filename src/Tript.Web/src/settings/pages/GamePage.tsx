// SPDX-License-Identifier: GPL-2.0-or-later
//
// The game page: the known games and their per-game overrides. How capture works is a Capture-page
// setting; this page only says which games depart from it. The game-capture timeout is the soft
// timeout: how long game capture waits for the game's window before falling back.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { DisplayCaptureMethod, GameSetting, RecordingMode } from '../settingsModel';
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

  const gameList = Array.isArray(settings.gameList) ? settings.gameList : [];

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
        <h3 className="subheading">Game overrides</h3>
        {gameList.length === 0 ? (
          <p className="muted small">No overrides yet — known games come from the project catalogue.</p>
        ) : (
          <p className="muted small">
            Stable game identities and known executables come from <code>data/games.json</code>. These
            settings provide per-game recording, capture, quality, and integration overrides.
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
                <span className="muted small">Recording mode</span>
                <SelectField
                  value={game.recordingModeOverride?.mode ?? ''}
                  options={RECORDING_MODE_OVERRIDES}
                  onChange={(value) => {
                    if (value === '') {
                      const { recordingModeOverride: _dropped, ...rest } = game;
                      patchGame(index, rest);
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
                      patchGame(index, rest);
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
        ))}

        <p className="muted small">
          Games and executables are maintained in the project catalogue. This page only edits per-game overrides.
        </p>
      </div>
    </div>
  );
}
