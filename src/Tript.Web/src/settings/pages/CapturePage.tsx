// SPDX-License-Identifier: GPL-2.0-or-later
//
// The capture page: which capture path supplies the picture, and which monitor the display layer
// uses. The monitor matters under `Auto` as well as `Display` — Auto shows the display layer until
// game capture hooks — so the picker is hidden only for `Game`, which has no display layer at all.

import type { SettingsPageName } from '../useSettings';
import type { CaptureSettings, DisplayCaptureMethod, DisplayInfo } from '../settingsModel';
import { Field, SelectField, TextField } from '../../components/ui/controls';
import {
  buildDisplayOptions,
  displayFieldMode,
  displaySelectValue,
  displaySelectionPatch,
  NOT_CONNECTED_SUFFIX,
} from '../displayModel';

const CAPTURE_METHODS: { value: DisplayCaptureMethod; label: string }[] = [
  { value: 'Auto', label: 'Auto — prefer game capture, fall back to the display' },
  { value: 'Game', label: 'Game — OBS game capture attached to the detected game' },
  { value: 'Display', label: 'Display — a specific monitor' },
];

const DISPLAY_HINT =
  'The monitor the display layer records. Under Auto it is what shows until game capture hooks.';

export function CapturePage({
  settings,
  update,
  page,
  availableDisplays,
}: {
  settings: CaptureSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  /** null = the host could not enumerate; [] = it did and found none. See `displayFieldMode`. */
  availableDisplays?: DisplayInfo[] | null;
}) {
  const showDisplay = settings.method === 'Auto' || settings.method === 'Display';
  const displays = availableDisplays ?? null;
  const mode = displayFieldMode(displays);
  const savedLabel = settings.displayLabel ?? null;

  return (
    <div className="settings-page" data-page="capture">
      <Field label="Capture method" hint="Game capture attaches to the detected game's process.">
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
              ? ` Your saved choice — ${savedLabel ?? settings.display}${NOT_CONNECTED_SUFFIX} — is kept and used again when it is back.`
              : ''}
          </p>
        </div>
      )}
    </div>
  );
}
