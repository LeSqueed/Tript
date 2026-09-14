// SPDX-License-Identifier: GPL-2.0-or-later

import type { SettingsPageName } from '../useSettings';
import type { HotkeyBinding, HotkeySettings } from '../settingsModel';
import { Field, TextField, Toggle } from '../../components/ui/controls';
import { HotkeyCaptureField } from './HotkeyCaptureField';

type BindingName = 'toggleRecording' | 'manualBookmark' | 'quickClip';

export function HotkeysPage({
  settings,
  update,
  page,
  bufferDurationSeconds,
}: {
  settings: HotkeySettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  bufferDurationSeconds?: number;
}) {
  function updateBinding(name: BindingName, binding: HotkeyBinding) {
    update(page, { [name]: binding });
  }

  const quickClipHint = bufferDurationSeconds
    ? `How many seconds before the hotkey press are included — capped at the replay buffer length (${bufferDurationSeconds}s).`
    : 'How many seconds before the hotkey press are included in the clip. Needs a replay buffer enabled.';

  return (
    <div className="settings-page" data-page="hotkeys">
      <section className="settings-section" aria-labelledby="hotkeys-heading">
        <h3 className="subheading" id="hotkeys-heading">Hotkeys</h3>
        <Toggle
          checked={settings.enabled}
          onChange={(checked) => update(page, { enabled: checked })}
          label="Enable global hotkeys"
        />
        <Field
          label="Toggle recording"
          hint="Starts or stops recording, even while another app has focus."
        >
          <HotkeyCaptureField
            binding={settings.toggleRecording}
            onChange={(binding) => updateBinding('toggleRecording', binding)}
            aria-label="Toggle recording hotkey"
          />
        </Field>
        <Field
          label="Manual bookmark"
          hint="Tags the current moment in an active recording. Not available in Replay Buffer Only mode."
        >
          <HotkeyCaptureField
            binding={settings.manualBookmark}
            onChange={(binding) => updateBinding('manualBookmark', binding)}
            aria-label="Manual bookmark hotkey"
          />
        </Field>
        <Field
          label="Quick clip"
          hint="Saves the last few seconds from the replay buffer. Needs a replay buffer enabled."
        >
          <HotkeyCaptureField
            binding={settings.quickClip}
            onChange={(binding) => updateBinding('quickClip', binding)}
            aria-label="Quick clip hotkey"
          />
        </Field>
        <Field
          label="Quick clip length"
          hint={quickClipHint}
        >
          <TextField
            type="number"
            value={settings.quickClipSeconds}
            onChange={(value) => update(page, { quickClipSeconds: Number(value) })}
            aria-label="Quick clip length in seconds"
            min={1}
          />
        </Field>
      </section>
    </div>
  );
}
