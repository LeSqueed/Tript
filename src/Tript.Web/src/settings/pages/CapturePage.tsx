// SPDX-License-Identifier: GPL-2.0-or-later
//
// The capture page: which capture path supplies the picture, and the optional display selection
// when the method is a display/monitor capture. The capture source matrix (spec/recorder.md):
// OBS game capture (attached to a game process) or display/monitor capture — both first-class in
// alpha, plus the auto choice.

import type { SettingsPageName } from '../useSettings';
import type { CaptureSettings, DisplayCaptureMethod } from '../settingsModel';
import { Field, SelectField, TextField } from '../form';

const CAPTURE_METHODS: { value: DisplayCaptureMethod; label: string }[] = [
  { value: 'Auto', label: 'Auto — prefer game capture, fall back to the display' },
  { value: 'Game', label: 'Game — OBS game capture attached to the detected game' },
  { value: 'Display', label: 'Display — a specific monitor' },
];

export function CapturePage({
  settings,
  update,
  page,
}: {
  settings: CaptureSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
}) {
  const showDisplay = settings.method === 'Display';
  return (
    <div className="settings-page" data-page="capture">
      <Field label="Capture method" hint="Game capture attaches to the detected game's process.">
        <SelectField
          value={settings.method}
          onChange={(value) => update(page, { method: value as DisplayCaptureMethod })}
          options={CAPTURE_METHODS}
        />
      </Field>

      {showDisplay && (
        <Field
          label="Display"
          hint="The monitor selected for display capture. A free-text identifier for the alpha."
        >
          <TextField
            value={settings.display ?? ''}
            onChange={(value) => update(page, { display: value || null })}
            placeholder="e.g. DP-1"
          />
        </Field>
      )}
    </div>
  );
}
