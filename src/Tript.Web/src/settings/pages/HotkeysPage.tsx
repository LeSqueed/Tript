// SPDX-License-Identifier: GPL-2.0-or-later

import type { SettingsPageName } from '../useSettings';
import type { HotkeyBinding, HotkeySettings } from '../settingsModel';
import { Button, Field, TextField, Toggle } from '../../components/ui/controls';
import { HotkeyCaptureField } from './HotkeyCaptureField';
import { usePlatformCapabilities } from '../../app/platformCapabilities';

const UNAVAILABLE_NOTE =
  'Global hotkeys are not available on this desktop. Tript needs the global shortcuts portal on Wayland, or an X11 session.';

const DESKTOP_SETTINGS_NOTE = "Change them in your desktop's keyboard shortcut settings, where Tript's shortcuts are listed.";

type BindingName = 'toggleRecording' | 'manualBookmark' | 'quickClip';

const BINDING_LABELS: Record<BindingName, string> = {
  toggleRecording: 'Toggle recording hotkey',
  manualBookmark: 'Manual bookmark hotkey',
  quickClip: 'Quick clip hotkey',
};

export function HotkeysPage({
  settings,
  update,
  page,
  bufferDurationSeconds,
  onConfigureDesktopShortcuts,
}: {
  settings: HotkeySettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  bufferDurationSeconds?: number;
  onConfigureDesktopShortcuts?: () => void;
}) {
  const capabilities = usePlatformCapabilities();
  const desktopManaged = capabilities.globalHotkeysManagedByDesktop;

  function updateBinding(name: BindingName, binding: HotkeyBinding) {
    update(page, { [name]: binding });
  }

  function bindingControl(name: BindingName) {
    if (desktopManaged) {
      const trigger = capabilities.desktopHotkeyTriggers[name];
      return (
        <p className="small" aria-label={BINDING_LABELS[name]}>
          {trigger ?? (settings.enabled ? 'Not assigned by the desktop yet' : 'Off')}
        </p>
      );
    }
    return (
      <HotkeyCaptureField
        binding={settings[name]}
        onChange={(binding) => updateBinding(name, binding)}
        aria-label={BINDING_LABELS[name]}
      />
    );
  }

  const quickClipHint = bufferDurationSeconds
    ? `How many seconds before the hotkey press are included, capped at the replay buffer length (${bufferDurationSeconds}s).`
    : 'How many seconds before the hotkey press are included in the clip. Needs a replay buffer enabled.';

  return (
    <div className="settings-page" data-page="hotkeys">
      <section className="settings-section" aria-labelledby="hotkeys-heading">
        <h3 className="subheading" id="hotkeys-heading">Hotkeys</h3>
        {!capabilities.globalHotkeys ? (
          <p className="muted small">{UNAVAILABLE_NOTE}</p>
        ) : (
        <>
        {capabilities.globalHotkeysNote && <p className="muted small">{capabilities.globalHotkeysNote}</p>}
        <Toggle
          checked={settings.enabled}
          onChange={(checked) => update(page, { enabled: checked })}
          label="Enable global hotkeys"
        />
        <Field
          label="Toggle recording"
          hint="Starts or stops recording, even while another app has focus."
        >
          {bindingControl('toggleRecording')}
        </Field>
        <Field
          label="Manual bookmark"
          hint="Tags the current moment in an active recording. Not available in Replay Buffer Only mode."
        >
          {bindingControl('manualBookmark')}
        </Field>
        <Field
          label="Quick clip"
          hint="Saves the last few seconds from the replay buffer. Needs a replay buffer enabled."
        >
          {bindingControl('quickClip')}
        </Field>
        {desktopManaged && settings.enabled && (
          capabilities.globalHotkeysConfigurable && onConfigureDesktopShortcuts ? (
            <Button onClick={onConfigureDesktopShortcuts}>Change shortcuts…</Button>
          ) : (
            <p className="muted small">{DESKTOP_SETTINGS_NOTE}</p>
          )
        )}
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
        </>
        )}
      </section>
    </div>
  );
}
