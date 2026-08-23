// SPDX-License-Identifier: GPL-2.0-or-later
//
// The buffer page configures the replay output used by Session + Replay Buffer mode.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import { Button, Field } from '../../components/ui/controls';

/** 1 MiB — the human-readable unit the size field is edited in. */
const MIB = 1024 * 1024;

/** Render a byte count in a compact human-readable form. */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return `${bytes}`;
  }
  if (bytes >= 1024 * 1024 * 1024) {
    return `${(bytes / (1024 * 1024 * 1024)).toFixed(1)} GiB`;
  }
  if (bytes >= 1024 * 1024) {
    return `${Math.round(bytes / (1024 * 1024))} MiB`;
  }
  if (bytes >= 1024) {
    return `${Math.round(bytes / 1024)} KiB`;
  }
  return `${bytes} B`;
}

export function BufferPage({
  settings,
  update,
  page,
  externalPushCount,
}: {
  settings: import('../settingsModel').BufferSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  externalPushCount: number;
}) {
  const [sizeMiB, setSizeMiB] = useState<string>(String(Math.round(settings.maxSizeBytes / MIB)));
  const [durationSeconds, setDurationSeconds] = useState<string>(String(settings.duration));

  // Re-sync drafts from the model only on an external push, never on the echo of our own edit.
  useEffect(() => {
    setSizeMiB(String(Math.round(settings.maxSizeBytes / MIB)));
    setDurationSeconds(String(settings.duration));
  }, [externalPushCount, settings.maxSizeBytes, settings.duration]);

  function commitSize() {
    const parsed = Number(sizeMiB);
    if (Number.isFinite(parsed) && parsed > 0) {
      update(page, { maxSizeBytes: Math.round(parsed * MIB) });
    } else {
      setSizeMiB(String(Math.round(settings.maxSizeBytes / MIB)));
    }
  }

  function commitDuration() {
    const parsed = Number(durationSeconds);
    if (Number.isFinite(parsed) && parsed > 0) {
      update(page, { duration: Math.round(parsed) });
    } else {
      setDurationSeconds(String(settings.duration));
    }
  }

  return (
    <div className="settings-page" data-page="buffer">
      <p className="settings-page-note">
        The replay buffer runs when the recording mode is set to Session + Replay Buffer. These
        settings control its duration and maximum size.
      </p>

      <Field label="Buffer duration" hint="How far back the rolling buffer reaches, in seconds.">
        <input
          type="number"
          className="input"
          min={1}
          value={durationSeconds}
          onChange={(event) => setDurationSeconds(event.target.value)}
          onBlur={commitDuration}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitDuration();
            }
          }}
          aria-label="Buffer duration"
        />
      </Field>

      <Field
        label="Maximum buffer size"
        hint={`Edited in MiB; stored as bytes. Currently ${formatBytes(settings.maxSizeBytes)}.`}
      >
        <input
          type="number"
          className="input"
          min={1}
          value={sizeMiB}
          onChange={(event) => setSizeMiB(event.target.value)}
          onBlur={commitSize}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitSize();
            }
          }}
          aria-label="Maximum buffer size"
        />
      </Field>

      <div className="settings-actions">
        <Button onClick={() => update(page, { duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 })}>
          Reset to defaults
        </Button>
      </div>
    </div>
  );
}
