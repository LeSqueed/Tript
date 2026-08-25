// SPDX-License-Identifier: GPL-2.0-or-later
//
// Desktop-shell settings: startup, window/tray behavior, and native notifications. These values
// persist on every platform, while only the desktop shell can apply Windows-specific effects.

import type { SettingsPageName } from '../useSettings';
import type { GeneralSettings } from '../settingsModel';
import { Field, SelectField, Toggle } from '../../components/ui/controls';

const STARTUP_VISIBILITY = [
  { value: 'Window', label: 'Open the Tript window' },
  { value: 'Minimized', label: 'Start minimized' },
  { value: 'Tray', label: 'Start hidden in the tray' },
];

const MINIMIZE_BEHAVIOR = [
  { value: 'Taskbar', label: 'Minimize to the taskbar' },
  { value: 'Tray', label: 'Hide to the tray' },
];

const CLOSE_BEHAVIOR = [
  { value: 'Exit', label: 'Exit Tript' },
  { value: 'HideToTray', label: 'Hide Tript to the tray' },
];

export function GeneralPage({
  settings,
  update,
  page,
}: {
  settings: GeneralSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
}) {
  const notifications = settings.notifications ?? {
    enabled: true,
    recordingStarted: true,
    recordingStopped: true,
    errors: true,
    recovery: true,
  };

  function updateNotification(name: keyof GeneralSettings['notifications'], value: boolean) {
    update(page, { notifications: { [name]: value } });
  }

  return (
    <div className="settings-page" data-page="general">
      <section className="settings-section" aria-labelledby="general-startup-heading">
        <h3 className="subheading" id="general-startup-heading">Startup</h3>
        <Toggle
          checked={settings.startWithWindows}
          onChange={(checked) => update(page, { startWithWindows: checked })}
          label="Start with Windows"
        />
        <Field label="Startup visibility" hint="Choose whether the Windows desktop shell opens a window when it starts. Other platforms use their normal window behavior.">
          <SelectField
            value={settings.startupVisibility}
            onChange={(value) => update(page, { startupVisibility: value })}
            options={STARTUP_VISIBILITY}
            aria-label="Startup visibility"
          />
        </Field>
      </section>

      <section className="settings-section" aria-labelledby="general-clips-heading">
        <h3 className="subheading" id="general-clips-heading">Clips</h3>
        <Toggle
          checked={settings.convertHdrClipsToSdr === true}
          onChange={(checked) => update(page, { convertHdrClipsToSdr: checked })}
          label="Convert HDR clips to SDR"
        />
        <p className="muted small">Tone-map new clips from HDR footage to BT.709 SDR. Original recordings remain unchanged.</p>
      </section>

      <section className="settings-section" aria-labelledby="general-window-heading">
        <h3 className="subheading" id="general-window-heading">Window and tray</h3>
        <Field label="Minimize button" hint="The native minimize button can leave Tript available in the tray.">
          <SelectField
            value={settings.minimizeBehavior}
            onChange={(value) => update(page, { minimizeBehavior: value })}
            options={MINIMIZE_BEHAVIOR}
            aria-label="Minimize button"
          />
        </Field>
        <Field label="When closing Tript" hint="An active recording is stopped before Tript exits or hides to the tray.">
          <SelectField
            value={settings.closeBehavior}
            onChange={(value) => update(page, { closeBehavior: value })}
            options={CLOSE_BEHAVIOR}
            aria-label="When closing Tript"
          />
        </Field>
      </section>

      <section className="settings-section" aria-labelledby="general-notification-heading">
        <h3 className="subheading" id="general-notification-heading">Notifications</h3>
        <Toggle
          checked={notifications.enabled}
          onChange={(checked) => updateNotification('enabled', checked)}
          label="Enable desktop notifications"
        />
        <Toggle
          checked={notifications.recordingStarted}
          onChange={(checked) => updateNotification('recordingStarted', checked)}
          label="Recording started"
          disabled={!notifications.enabled}
        />
        <Toggle
          checked={notifications.recordingStopped}
          onChange={(checked) => updateNotification('recordingStopped', checked)}
          label="Recording stopped"
          disabled={!notifications.enabled}
        />
        <Toggle
          checked={notifications.errors}
          onChange={(checked) => updateNotification('errors', checked)}
          label="Errors"
          disabled={!notifications.enabled}
        />
        <Toggle
          checked={notifications.recovery}
          onChange={(checked) => updateNotification('recovery', checked)}
          label="Unfinished recording found"
          disabled={!notifications.enabled}
        />
      </section>
    </div>
  );
}
