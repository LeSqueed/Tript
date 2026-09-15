// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { GeneralSettings, RecordingSettings } from '../settingsModel';
import type { IpcClient } from '../../ipc/websocketClient';
import type { UpdateProgressMessage } from '../../ipc/protocol';
import { Button, Checkbox, Field, SelectField, Toggle } from '../../components/ui/controls';

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

const TRASH_RETENTION = [
  { value: '24', label: '1 day' },
  { value: '168', label: '7 days' },
  { value: '720', label: '30 days' },
  { value: '0', label: 'Never' },
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

  function updateNotification(name: keyof GeneralSettings['notifications'], value: boolean) {
    update(page, { notifications: { [name]: value } });
  }

  const storedTrashRetention = recording.trashRetentionHours ?? 24;
  const trashRetentionValue = String(storedTrashRetention <= 0 ? 0 : storedTrashRetention);
  const trashRetentionOptions = TRASH_RETENTION.some((option) => option.value === trashRetentionValue)
    ? TRASH_RETENTION
    : [...TRASH_RETENTION, { value: trashRetentionValue, label: `${trashRetentionValue} hours (current)` }];

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
        <Field
          label="Empty the trash after"
          hint="Deleted items are removed from the trash permanently after this time. Never keeps them until you empty the trash by hand."
        >
          <SelectField
            value={trashRetentionValue}
            onChange={(value) => update('recording', { trashRetentionHours: Number(value) })}
            options={trashRetentionOptions}
            aria-label="Empty the trash after"
          />
        </Field>
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
        <div className="field">
          <span className="field-label">Recording started</span>
          <div className="toggle-pair">
            <Toggle
              checked={notifications.recordingStarted}
              onChange={(checked) => updateNotification('recordingStarted', checked)}
              label="Show notification"
              disabled={!notifications.enabled}
            />
            <Toggle
              checked={notifications.recordingStartedSound}
              onChange={(checked) => updateNotification('recordingStartedSound', checked)}
              label="Play sound"
              disabled={!notifications.enabled}
            />
          </div>
        </div>
        <div className="field">
          <span className="field-label">Recording stopped</span>
          <div className="toggle-pair">
            <Toggle
              checked={notifications.recordingStopped}
              onChange={(checked) => updateNotification('recordingStopped', checked)}
              label="Show notification"
              disabled={!notifications.enabled}
            />
            <Toggle
              checked={notifications.recordingStoppedSound}
              onChange={(checked) => updateNotification('recordingStoppedSound', checked)}
              label="Play sound"
              disabled={!notifications.enabled}
            />
          </div>
        </div>
        <div className="field">
          <span className="field-label">Errors</span>
          <div className="toggle-pair">
            <Toggle
              checked={notifications.errors}
              onChange={(checked) => updateNotification('errors', checked)}
              label="Show notification"
              disabled={!notifications.enabled}
            />
            <Toggle
              checked={notifications.errorsSound}
              onChange={(checked) => updateNotification('errorsSound', checked)}
              label="Play sound"
              disabled={!notifications.enabled}
            />
          </div>
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
