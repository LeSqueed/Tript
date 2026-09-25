// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { GeneralSettings, RecordingSettings } from '../settingsModel';
import type { IpcClient } from '../../ipc/websocketClient';
import type { UpdateProgressMessage } from '../../ipc/protocol';
import { Button, Checkbox, Field, SelectField, Toggle } from '../../components/ui/controls';
import { usePlatformCapabilities } from '../../app/platformCapabilities';

const STARTUP_VISIBILITY = [
  { value: 'Window', label: 'Open the Tript window' },
  { value: 'Minimized', label: 'Start minimized' },
  { value: 'Tray', label: 'Start hidden in the tray' },
];

const MINIMIZE_BEHAVIOR = [
  { value: 'Taskbar', label: 'Minimize to the taskbar' },
  { value: 'Tray', label: 'Hide to the tray' },
];

type NotificationToggle =
  | 'enabled'
  | 'recordingStarted'
  | 'recordingStartedSound'
  | 'recordingStopped'
  | 'recordingStoppedSound'
  | 'errors'
  | 'errorsSound';

const NOTIFICATION_ROWS: { label: string; notification: NotificationToggle; sound: NotificationToggle }[] = [
  { label: 'Recording started', notification: 'recordingStarted', sound: 'recordingStartedSound' },
  { label: 'Recording stopped', notification: 'recordingStopped', sound: 'recordingStoppedSound' },
  { label: 'Errors', notification: 'errors', sound: 'errorsSound' },
];

const CLOSE_BEHAVIOR = [
  { value: 'Exit', label: 'Exit Tript' },
  { value: 'HideToTray', label: 'Hide Tript to the tray' },
];

export function GeneralPage({
  client,
  settings,
  recording,
  update,
  page,
  appVersion,
}: {
  client: IpcClient;
  settings: GeneralSettings;
  recording: RecordingSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  appVersion?: string | null;
}) {
  const [updateStatus, setUpdateStatus] = useState<UpdateProgressMessage | null>(null);
  const capabilities = usePlatformCapabilities();
  const startupVisibilityOptions = capabilities.tray
    ? STARTUP_VISIBILITY
    : STARTUP_VISIBILITY.filter((option) => option.value !== 'Tray');
  const startupVisibility = !capabilities.tray && settings.startupVisibility === 'Tray'
    ? 'Minimized'
    : settings.startupVisibility;
  const startupVisibilityHint = capabilities.platform === 'windows'
    ? 'Choose whether the Windows desktop shell opens a window when it starts. Other platforms use their normal window behavior.'
    : 'Choose whether the Tript window opens or starts minimized.';
  const showNotificationToggles = capabilities.notifications;
  const showSoundToggles = capabilities.notificationSounds;

  useEffect(() => client.on('updateProgress', (content) => {
    setUpdateStatus(content as UpdateProgressMessage | null);
  }), [client]);

  const notifications = settings.notifications ?? {
    enabled: true,
    recordingStarted: true,
    recordingStartedSound: true,
    recordingStopped: true,
    recordingStoppedSound: true,
    errors: true,
    errorsSound: true,
  };

  function updateNotification(name: NotificationToggle, value: boolean) {
    update(page, { notifications: { [name]: value } });
  }

  const isChecking = updateStatus?.stage === 'checking' || updateStatus?.stage === 'downloading';
  const updateStatusText = describeUpdateStatus(updateStatus);
  const updateReleaseUrl = (updateStatus?.stage === 'ready' || updateStatus?.stage === 'available')
    ? updateStatus.releaseUrl
    : undefined;

  return (
    <div className="settings-page" data-page="general">
      <section className="settings-section" aria-labelledby="general-updates-heading">
        <h3 className="subheading" id="general-updates-heading">Updates</h3>
        <p className="muted small">Tript {appVersion ?? 'unknown version'}</p>
        <Toggle
          checked={settings.checkForUpdatesAutomatically !== false}
          onChange={(checked) => update(page, { checkForUpdatesAutomatically: checked })}
          label="Check for updates automatically"
        />
        <div className="field">
          <Button onClick={() => client.send('CheckForUpdates')} disabled={isChecking}>
            {isChecking ? 'Checking…' : 'Check for Updates'}
          </Button>
          {updateStatus?.stage === 'ready' && (
            <Button onClick={() => client.send('ApplyUpdate')}>Restart &amp; update</Button>
          )}
          {updateReleaseUrl && (
            <Button variant="ghost" onClick={() => client.send('OpenInBrowser', { url: updateReleaseUrl })}>
              View on GitHub
            </Button>
          )}
        </div>
        {updateStatusText && <span className="field-hint">{updateStatusText}</span>}
      </section>

      <section className="settings-section" aria-labelledby="general-startup-heading">
        <h3 className="subheading" id="general-startup-heading">Startup</h3>
        {capabilities.startWithSystem && (
        <Toggle
          checked={settings.startWithWindows}
          onChange={(checked) => update(page, { startWithWindows: checked })}
          label="Start with Windows"
        />
        )}
        <Field label="Startup visibility" hint={startupVisibilityHint}>
          <SelectField
            value={startupVisibility}
            onChange={(value) => update(page, { startupVisibility: value })}
            options={startupVisibilityOptions}
            aria-label="Startup visibility"
          />
        </Field>
      </section>

      <section className="settings-section" aria-labelledby="general-clips-heading">
        <h3 className="subheading" id="general-clips-heading">Clips</h3>
        <div className="field">
          <Toggle
            checked={settings.convertHdrClipsToSdr === true}
            onChange={(checked) => update(page, { convertHdrClipsToSdr: checked })}
            label="Convert HDR clips to SDR"
          />
          <span className="field-hint">New clips made from HDR footage are converted for players and displays that expect SDR. Original recordings stay as they were.</span>
        </div>
        <Field
          label="Delete linked highlights by default"
          hint="Preselects the option to delete linked highlights when deleting a session. Favourited highlights are always kept."
        >
          <Checkbox
            checked={recording.deleteLinkedHighlightsByDefault === true}
            onChange={(checked) => update('recording', { deleteLinkedHighlightsByDefault: checked })}
            aria-label="Delete linked highlights by default"
          />
        </Field>
      </section>

      {capabilities.hideToTray && (
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
        <Field label="When closing Tript" hint="Hiding to the tray keeps an active recording going. Exiting stops and saves it first.">
          <SelectField
            value={settings.closeBehavior}
            onChange={(value) => update(page, { closeBehavior: value })}
            options={CLOSE_BEHAVIOR}
            aria-label="When closing Tript"
          />
        </Field>
      </section>
      )}

      {(showNotificationToggles || showSoundToggles) && (
      <section className="settings-section" aria-labelledby="general-notification-heading">
        <h3 className="subheading" id="general-notification-heading">Notifications</h3>
        <Toggle
          checked={notifications.enabled}
          onChange={(checked) => updateNotification('enabled', checked)}
          label="Enable desktop notifications"
        />
        {NOTIFICATION_ROWS.map((row) => (
          <div className="field" key={row.label}>
            <span className="field-label">{row.label}</span>
            <div className="toggle-pair">
              {showNotificationToggles && (
                <Toggle
                  checked={notifications[row.notification]}
                  onChange={(checked) => updateNotification(row.notification, checked)}
                  label="Show notification"
                  disabled={!notifications.enabled}
                />
              )}
              {showSoundToggles && (
                <Toggle
                  checked={notifications[row.sound]}
                  onChange={(checked) => updateNotification(row.sound, checked)}
                  label="Play sound"
                  disabled={!notifications.enabled}
                />
              )}
            </div>
          </div>
        ))}
      </section>
      )}

      <section className="settings-section" aria-labelledby="general-diagnostics-heading">
        <h3 className="subheading" id="general-diagnostics-heading">Diagnostics</h3>
        <p className="muted small">
          Logs record what Tript was doing when something went wrong. If you report a problem, include the
          most recent log file. Logs contain folder paths, file names and game names from this PC, so look
          through one before sharing it.
        </p>
        <div className="field">
          <Button onClick={() => client.send('OpenLogFolder')}>Open log folder</Button>
        </div>
      </section>
    </div>
  );
}

function describeUpdateStatus(status: UpdateProgressMessage | null): string | null {
  if (!status) return null;
  switch (status.stage) {
    case 'checking':
      return 'Checking for updates…';
    case 'upToDate':
      return "You're up to date.";
    case 'available':
      return `Tript ${status.version ?? ''} is available.`.replace('  ', ' ');
    case 'downloading': {
      if (typeof status.completedBytes === 'number' && typeof status.totalBytes === 'number' && status.totalBytes > 0) {
        const percent = Math.min(100, Math.round((status.completedBytes / status.totalBytes) * 100));
        return `Downloading update… ${percent}%.`;
      }
      return 'Downloading update…';
    }
    case 'ready':
      return `Tript ${status.version ?? ''} is ready. Restart to finish updating.`.replace('  ', ' ');
    case 'error':
      return status.error ?? 'Could not check for updates.';
    default:
      return null;
  }
}
