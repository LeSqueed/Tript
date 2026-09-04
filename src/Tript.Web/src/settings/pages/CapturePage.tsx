// SPDX-License-Identifier: GPL-2.0-or-later
//
// The capture page: which capture path supplies the picture, and which monitor the display layer
// uses. The monitor matters under `Auto` as well as `Display`, because `Auto` shows the display
// layer until game capture takes over, so the picker is hidden only for `Game`, which has no
// display layer at all. The game-capture timeout now lives here too: it is the one knob about how
// game capture behaves, so it sits in this page's advanced disclosure and patches the `game` page.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { CaptureSettings, DisplayCaptureMethod, DisplayInfo, GameSettings } from '../settingsModel';
import { Field, SelectField, TextField } from '../../components/ui/controls';
import {
  buildDisplayOptions,
  displayFieldMode,
  displaySelectValue,
  displaySelectionPatch,
  NOT_CONNECTED_SUFFIX,
} from '../displayModel';

const CAPTURE_METHODS: { value: DisplayCaptureMethod; label: string }[] = [
  { value: 'Auto', label: 'Automatic' },
  { value: 'Game', label: 'Game window' },
  { value: 'Display', label: 'A specific monitor' },
];

const DISPLAY_HINT =
  'The monitor the display capture records. Under Automatic it is what shows until game capture takes over.';

export function CapturePage({
  settings,
  game,
  update,
  page,
  availableDisplays,
  externalPushCount,
}: {
  settings: CaptureSettings;
  game: GameSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  /** null = the host could not enumerate; [] = it did and found none. See `displayFieldMode`. */
  availableDisplays?: DisplayInfo[] | null;
  externalPushCount: number;
}) {
  const showDisplay = settings.method === 'Auto' || settings.method === 'Display';
  const displays = availableDisplays ?? null;
  const mode = displayFieldMode(displays);
  const savedLabel = settings.displayLabel ?? null;

  const [timeoutSeconds, setTimeoutSeconds] = useState<string>(String(game.gameCaptureTimeout));

  // Re-sync only on an external push. A delayed self echo must not erase a newer draft.
  useEffect(() => {
    setTimeoutSeconds(String(game.gameCaptureTimeout));
  }, [externalPushCount]);

  function commitTimeout() {
    const parsed = Number(timeoutSeconds);
    if (Number.isFinite(parsed) && parsed > 0) {
      const timeout = Math.round(parsed);
      setTimeoutSeconds(String(timeout));
      update('game', { gameCaptureTimeout: timeout });
    } else {
      setTimeoutSeconds(String(game.gameCaptureTimeout));
    }
  }

  return (
    <div className="settings-page" data-page="capture">
      <Field
        label="Capture method"
        hint="How Tript gets the picture. Automatic uses the game's own window when it can and the monitor otherwise; Game window attaches OBS game capture to the detected game; A specific monitor records that monitor."
      >
        <SelectField
          value={settings.method}
          onChange={(value) => update(page, { method: value as DisplayCaptureMethod })}
          options={CAPTURE_METHODS}
        />
      </Field>

      {showDisplay && mode === 'picker' && (
        <Field label="Display" hint={DISPLAY_HINT}>
          <SelectField
            data-testid="capture-display-select"
            value={displaySelectValue(settings.display)}
            onChange={(value) => update(page, displaySelectionPatch(value, displays ?? [], savedLabel))}
            options={buildDisplayOptions(displays ?? [], settings.display, savedLabel)}
          />
        </Field>
      )}

      {showDisplay && mode === 'text' && (
        <Field
          label="Display"
          hint={`${DISPLAY_HINT} This machine's monitors could not be listed, so the identifier is typed.`}
        >
          <TextField
            data-testid="capture-display-input"
            value={settings.display ?? ''}
            // The label is dropped with the id it named: nothing here can supply a name for a typed id,
            // and keeping the old one would let a warning name the wrong monitor.
            onChange={(value) => update(page, { display: value || null, displayLabel: null })}
            placeholder="e.g. DP-1"
          />
        </Field>
      )}

      {showDisplay && mode === 'none' && (
        <div className="field" data-testid="capture-display-none">
          <span className="field-label">Display</span>
          <p className="muted small">
            No monitors were detected on this machine. Recording uses whichever monitor the system
            reports as primary.
            {settings.display
              ? ` Your saved choice, ${savedLabel ?? settings.display}${NOT_CONNECTED_SUFFIX}, is kept and used again when it is back.`
              : ''}
          </p>
        </div>
      )}

      <details className="settings-advanced">
        <summary>Advanced</summary>
        <div className="settings-advanced-body">
          <Field
            label="Game-capture timeout"
            hint="How long Tript waits before warning that game capture has not connected, in seconds. It keeps waiting until the game is ready or recording is stopped."
          >
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
        </div>
      </details>
    </div>
  );
}
