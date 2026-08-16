// SPDX-License-Identifier: GPL-2.0-or-later
//
// The recording page: the session recording itself. Mode (session/buffer/hybrid), resolution,
// frame rate, encoder and quality. The encoder list filters to what the machine supports on the
// backend (spec/recorder.md) — the frontend treats the encoder field as a free text/key field and
// the backend settles availability.
//
// Free-text fields (resolution, frame rate) hold local drafts so the user's typing is never
// clobbered by the echo of their own edit. The draft commits on blur or Enter; it re-syncs from
// the model only when an *external* push arrives (`externalPushCount` changes).

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { RecordingMode } from '../settingsModel';
import { ActionButton, Field, GhostButton, SelectField, TextField } from '../form';

const RECORDING_MODES: { value: RecordingMode; label: string }[] = [
  { value: 'Session', label: 'Session — one continuous recording' },
  { value: 'Buffer', label: 'Buffer — rolling replay only, nothing written until saved' },
  { value: 'Hybrid', label: 'Hybrid — session and rolling buffer at once' },
];

/** The quality profile applied when a game has no override of its own. */
const QUALITY_OPTIONS = [
  { value: '3', label: 'Low' },
  { value: '5', label: 'Medium' },
  { value: '10', label: 'High' },
  { value: '18', label: 'Max' },
];

export function RecordingPage({
  settings,
  update,
  page,
  externalPushCount,
}: {
  settings: import('../settingsModel').RecordingSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  externalPushCount: number;
}) {
  const [resolution, setResolution] = useState<string>(
    `${settings.resolutionWidth}x${settings.resolutionHeight}`,
  );
  const [fpsDraft, setFpsDraft] = useState<string>(String(settings.fps));

  // Re-sync drafts from the model only on an external push, never on the echo of our own edit.
  useEffect(() => {
    setResolution(`${settings.resolutionWidth}x${settings.resolutionHeight}`);
    setFpsDraft(String(settings.fps));
  }, [externalPushCount, settings.resolutionWidth, settings.resolutionHeight, settings.fps]);

  function commitResolution() {
    const match = /^\s*(\d+)\s*[x×]\s*(\d+)\s*$/.exec(resolution);
    if (match) {
      const width = Number(match[1]);
      const height = Number(match[2]);
      update(page, { resolutionWidth: width, resolutionHeight: height });
    } else {
      setResolution(`${settings.resolutionWidth}x${settings.resolutionHeight}`);
    }
  }

  function commitFps() {
    const parsed = Number(fpsDraft);
    if (Number.isFinite(parsed) && parsed > 0) {
      update(page, { fps: Math.round(Math.min(480, Math.max(1, parsed))) });
    } else {
      setFpsDraft(String(settings.fps));
    }
  }

  return (
    <div className="settings-page" data-page="recording">
      <Field label="Recording mode" hint="Hybrid is the default, globally and per game.">
        <SelectField
          value={settings.mode}
          onChange={(value) => update(page, { mode: value as RecordingMode })}
          options={RECORDING_MODES}
        />
      </Field>

      <Field label="Resolution" hint="Width × height of the recorded picture.">
        <input
          type="text"
          className="settings-input"
          value={resolution}
          onChange={(event) => setResolution(event.target.value)}
          onBlur={commitResolution}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitResolution();
            }
          }}
          aria-label="Resolution"
        />
      </Field>

      <Field label="Frame rate" hint="Frames per second.">
        <input
          type="number"
          className="settings-input"
          min={1}
          value={fpsDraft}
          onChange={(event) => setFpsDraft(event.target.value)}
          onBlur={commitFps}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitFps();
            }
          }}
          aria-label="Frame rate"
        />
      </Field>

      <Field label="Encoder" hint="The video encoder. Availability is settled on the backend.">
        <TextField
          value={settings.encoder}
          onChange={(value) => update(page, { encoder: value })}
          placeholder="x264"
        />
      </Field>

      <Field label="Quality" hint="Applied when a game has no override of its own.">
        <SelectField
          value={String(settings.quality)}
          onChange={(value) => update(page, { quality: Number(value) })}
          options={QUALITY_OPTIONS}
        />
      </Field>

      <div className="settings-actions">
        <ActionButton onClick={() => update(page, { mode: 'Hybrid' })}>Reset to defaults</ActionButton>
        <GhostButton onClick={commitResolution}>Apply resolution</GhostButton>
      </div>
    </div>
  );
}
