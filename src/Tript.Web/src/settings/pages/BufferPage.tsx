// SPDX-License-Identifier: GPL-2.0-or-later
//
// The buffer page — a first-class settings surface even though the buffer itself is deferred in
// alpha (design decision 2026-08-15). Enable/disable and configure the rolling replay buffer
// independently of the session.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import { ActionButton, Field } from '../form';

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
      <label className="settings-field settings-toggle">
        <span className="settings-field-label">Enable rolling buffer</span>
        <input
          type="checkbox"
          className="settings-checkbox"
          checked={settings.enabled}
          onChange={(event) => update(page, { enabled: event.target.checked })}
        />
      </label>
      <p className="settings-page-note">
        {settings.enabled ? (
          <>The buffer is enabled. It is a first-class setting surface; the alpha recorder does not act on it yet.</>
        ) : (
          <>The buffer is disabled — nothing is kept in memory until you turn it on.</>
        )}
      </p>

      <Field label="Buffer duration" hint="How far back the rolling buffer reaches, in seconds.">
        <input
          type="number"
          className="settings-input"
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
          className="settings-input"
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
        <ActionButton onClick={() => update(page, { duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 })}>
          Reset to defaults
        </ActionButton>
      </div>
    </div>
  );
}
