// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { RecordingSettings, StorageFullAction, StorageSettings } from '../settingsModel';
import type { StorageReportMessage, StorageStatusMessage } from '../../ipc/protocol';
import {
  Button,
  Field,
  SegmentedControl,
  SelectField,
  TextField,
  Toggle,
} from '../../components/ui/controls';
import { ConfirmDialog } from '../../components/ui/confirmDialog';
import { StatusDot } from '../../components/ui/Ui';
import { StorageBar } from '../../components/storage/StorageBar';
import {
  formatStorageSize,
  fromGigabytes,
  gameUsageLabel,
  toGigabytes,
} from '../../components/storage/storageModel';

const WHEN_FULL_SEGMENTS: { value: StorageFullAction; label: string }[] = [
  { value: 'PauseRecording', label: 'Pause recording' },
  { value: 'ReclaimOldest', label: 'Remove the oldest' },
];

const WHEN_FULL_DETAIL: Record<StorageFullAction, string> = {
  PauseRecording:
    'Tript stops recording and waits. Nothing on disk is touched until you say so.',
  ReclaimOldest:
    'Tript empties the trash, then removes the oldest sessions, so recording keeps going. Favourites are never removed.',
};

const TRASH_RETENTION = [
  { value: '24', label: '1 day' },
  { value: '168', label: '7 days' },
  { value: '720', label: '30 days' },
  { value: '0', label: 'Never' },
];

const MINIMUM_GIGABYTES = 1;
const MAXIMUM_GIGABYTES = 2048;
const GAMES_BEFORE_COLLAPSE = 6;

function nativeDirectoryExample(): { placeholder: string; defaultLabel: string } {
  const windows = typeof navigator !== 'undefined' && /Windows/i.test(navigator.userAgent);
  return windows
    ? { placeholder: String.raw`D:\Recordings`, defaultLabel: String.raw`Videos\Tript` }
    : { placeholder: '/home/you/Videos/Tript', defaultLabel: 'Videos/Tript' };
}

export function StoragePage({
  settings,
  recording,
  update,
  page,
  externalPushCount,
  status,
  report,
  onReclaim,
  onRefresh,
  onClearTrash,
  onBrowse,
}: {
  settings: StorageSettings;
  recording: RecordingSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  externalPushCount: number;
  status: StorageStatusMessage | null;
  report: StorageReportMessage | null;
  onReclaim: () => void;
  onRefresh: () => void;
  onClearTrash: () => void;
  onBrowse: () => void;
}) {
  const [reservedGigabytes, setReservedGigabytes] = useState(
    String(toGigabytes(settings.minimumFreeBytes)),
  );
  const [confirmingReclaim, setConfirmingReclaim] = useState(false);
  const [confirmingClearTrash, setConfirmingClearTrash] = useState(false);
  const [showAllGames, setShowAllGames] = useState(false);

  useEffect(() => {
    setReservedGigabytes(String(toGigabytes(settings.minimumFreeBytes)));
  }, [externalPushCount, settings.minimumFreeBytes]);

  useEffect(() => {
    onRefresh();
  }, []);

  function commitReserved() {
    const parsed = Number(reservedGigabytes);
    if (!Number.isFinite(parsed) || parsed < MINIMUM_GIGABYTES || parsed > MAXIMUM_GIGABYTES) {
      setReservedGigabytes(String(toGigabytes(settings.minimumFreeBytes)));
      return;
    }
    setReservedGigabytes(String(parsed));
    update(page, { minimumFreeBytes: fromGigabytes(parsed), policyConfirmed: true });
  }

  function chooseWhenFull(whenFull: StorageFullAction) {
    update(page, { whenFull, policyConfirmed: true });
  }

  const directoryExample = nativeDirectoryExample();

  const storedRetention = recording.trashRetentionHours ?? 24;
  const retentionValue = String(storedRetention <= 0 ? 0 : storedRetention);
  const retentionOptions = TRASH_RETENTION.some((option) => option.value === retentionValue)
    ? TRASH_RETENTION
    : [...TRASH_RETENTION, { value: retentionValue, label: `${retentionValue} hours (current)` }];

  const trashCount = report?.trashCount ?? 0;
  const games = report?.games ?? [];
  const shownGames = showAllGames ? games : games.slice(0, GAMES_BEFORE_COLLAPSE);

  const pressure = status?.pressure ?? 'unknown';
  const tone = pressure === 'critical' ? 'error' : pressure === 'warning' ? 'warning' : 'success';
  const headline = status?.recordingBlocked
    ? 'Recording is on hold, there is not enough free space'
    : pressure === 'warning'
      ? 'Space is running low'
      : pressure === 'unknown'
        ? 'The recording drive could not be read'
        : 'There is room to record';

  return (
    <div className="settings-page">
      <div className="storage-page-summary">
        <div className="storage-headline">
          <StatusDot tone={tone} />
          <strong>{headline}</strong>
          {status && (
            <span className="muted small">
              {formatStorageSize(status.freeBytes)} free on {status.volumeRoot ?? status.root}
            </span>
          )}
        </div>
        {status?.scratchLow && (
          <p className="muted small" data-testid="storage-scratch-warning">
            Replays are saved on {status.scratchRoot}, which has only{' '}
            {formatStorageSize(status.scratchFreeBytes)} left. Instant replay may fail to save until
            that drive has more room.
          </p>
        )}
        {report ? (
          <StorageBar report={report} />
        ) : (
          <p className="muted small">Working out what is using the recording folder...</p>
        )}
      </div>

      <Field
        label="Recording folder"
        hint={`Where recordings and highlights are saved. Leave empty for the default (${directoryExample.defaultLabel}).`}
      >
        <span className="settings-row">
          <TextField
            value={recording.outputDirectory ?? ''}
            onChange={(value) => update('recording', { outputDirectory: value === '' ? null : value })}
            placeholder={`e.g. ${directoryExample.placeholder}`}
            aria-label="Recording folder"
          />
          <Button variant="ghost" onClick={onBrowse} title="Choose the recording folder with a native picker">
            Browse
          </Button>
        </span>
      </Field>

      <Field
        label="Keep this much free"
        hint="Tript stops filling the drive once this much space is left, so Windows and your games keep working."
      >
        <TextField
          value={reservedGigabytes}
          aria-label="Keep this much free in GB"
          onChange={setReservedGigabytes}
          onBlur={commitReserved}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitReserved();
            }
          }}
        />
        <span className="muted small">GB</span>
      </Field>

      <Field label="When the drive is nearly full" hint={WHEN_FULL_DETAIL[settings.whenFull]}>
        <SegmentedControl
          label="When the drive is nearly full"
          value={settings.whenFull}
          segments={WHEN_FULL_SEGMENTS}
          onChange={chooseWhenFull}
        />
      </Field>

      <div className="settings-section">
        <Toggle
          label="Keep sharing the game picture with OBS when recording is on hold"
          checked={settings.keepSharingWhenFull}
          onChange={(checked) => update(page, { keepSharingWhenFull: checked })}
        />
        <p className="muted small">
          Sharing writes nothing to disk, so your stream keeps its picture even when Tript cannot
          record.
        </p>
      </div>

      <Field
        label="Empty the trash after"
        hint="Deleted items are removed from the trash permanently after this time. Never keeps them until you clear the trash by hand."
      >
        <SelectField
          value={retentionValue}
          onChange={(value) => update('recording', { trashRetentionHours: Number(value) })}
          options={retentionOptions}
          aria-label="Empty the trash after"
        />
      </Field>

      <div className="settings-section">
        <h2 className="subheading">Free up space</h2>
        <p className="muted small">
          Freeing up space empties the trash first, then removes the oldest sessions. Favourites are
          never removed.
        </p>
        <div className="settings-actions">
          <Button variant="ghost" onClick={() => setConfirmingReclaim(true)}>
            Free up space now
          </Button>
          <Button variant="danger" disabled={trashCount === 0} onClick={() => setConfirmingClearTrash(true)}>
            Clear trash
          </Button>
          <Button variant="ghost" onClick={onRefresh}>
            Recount
          </Button>
        </div>
      </div>

      {games.length > 0 && (
        <div className="settings-section">
          <h2 className="subheading">Space per game</h2>
          <table className="storage-games">
            <thead>
              <tr>
                <th>Game</th>
                <th>Sessions</th>
                <th>Highlights</th>
                <th>Clips</th>
                <th>Total</th>
              </tr>
            </thead>
            <tbody>
              {shownGames.map((usage) => (
                <tr key={usage.gameId ?? usage.name ?? 'unlinked'}>
                  <td>{gameUsageLabel(usage)}</td>
                  <td>{formatStorageSize(usage.sessionBytes)}</td>
                  <td>{formatStorageSize(usage.highlightBytes)}</td>
                  <td>{formatStorageSize(usage.clipBytes)}</td>
                  <td>{formatStorageSize(usage.totalBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {games.length > GAMES_BEFORE_COLLAPSE && (
            <div className="settings-actions">
              <Button variant="ghost" onClick={() => setShowAllGames((shown) => !shown)}>
                {showAllGames ? 'Show fewer' : `Show all ${games.length} games`}
              </Button>
            </div>
          )}
        </div>
      )}

      {confirmingReclaim && (
        <ConfirmDialog
          title="Free up space now?"
          notice={
            'Tript empties the trash and then permanently removes the oldest sessions until there is '
            + `${formatStorageSize(status?.minimumFreeBytes ?? 0)} free. Favourites are kept. This cannot be undone.`
          }
          confirmLabel="Free up space"
          confirmVariant="danger"
          onConfirm={() => {
            setConfirmingReclaim(false);
            onReclaim();
          }}
          onCancel={() => setConfirmingReclaim(false)}
        />
      )}

      {confirmingClearTrash && (
        <ConfirmDialog
          title="Clear the trash?"
          notice={
            `This permanently deletes the ${trashCount} item(s) in the trash and frees `
            + `${formatStorageSize(report?.trashBytes ?? 0)}. It cannot be undone.`
          }
          confirmLabel="Clear trash"
          confirmVariant="danger"
          onConfirm={() => {
            setConfirmingClearTrash(false);
            onClearTrash();
          }}
          onCancel={() => setConfirmingClearTrash(false)}
        />
      )}
    </div>
  );
}
