// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type { BufferSettings, RecordingSettings } from '../settingsModel';
import { Button, Checkbox, Field, TextField } from '../../components/ui/controls';

const DEFAULT_CLIP_BEFORE_SECONDS = 5;
const DEFAULT_CLIP_AFTER_SECONDS = 8;

function parseSeconds(draft: string): number | null {
  const parsed = Number(draft);
  return Number.isFinite(parsed) && parsed > 0 ? Math.round(parsed) : null;
}

export function HighlightsPage({
  settings,
  recording,
  update,
  page,
  externalPushCount,
}: {
  settings: BufferSettings;
  recording: RecordingSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  page: SettingsPageName;
  externalPushCount: number;
}) {
  const bufferModeOff = recording.mode !== 'SessionWithReplayBuffer'
    && recording.mode !== 'ReplayBufferOnly';
  const [durationSeconds, setDurationSeconds] = useState<string>(String(settings.duration));
  const [beforeSeconds, setBeforeSeconds] = useState<string>(
    String(recording.automaticClipBeforeSeconds ?? DEFAULT_CLIP_BEFORE_SECONDS),
  );
  const [afterSeconds, setAfterSeconds] = useState<string>(
    String(recording.automaticClipAfterSeconds ?? DEFAULT_CLIP_AFTER_SECONDS),
  );

  useEffect(() => {
    setDurationSeconds(String(settings.duration));
    setBeforeSeconds(String(recording.automaticClipBeforeSeconds ?? DEFAULT_CLIP_BEFORE_SECONDS));
    setAfterSeconds(String(recording.automaticClipAfterSeconds ?? DEFAULT_CLIP_AFTER_SECONDS));
  }, [externalPushCount]);

  function commitDuration() {
    const parsed = Number(durationSeconds);
    if (Number.isFinite(parsed) && parsed > 0) {
      const duration = Math.round(parsed);
      setDurationSeconds(String(duration));
      update(page, { duration });
    } else {
      setDurationSeconds(String(settings.duration));
    }
  }

  function storedBeforeSeconds(): number {
    return recording.automaticClipBeforeSeconds ?? DEFAULT_CLIP_BEFORE_SECONDS;
  }

  function storedAfterSeconds(): number {
    return recording.automaticClipAfterSeconds ?? DEFAULT_CLIP_AFTER_SECONDS;
  }

  function commitBefore() {
    const before = parseSeconds(beforeSeconds);
    if (before === null) {
      setBeforeSeconds(String(storedBeforeSeconds()));
      return;
    }
    const currentAfter = parseSeconds(afterSeconds) ?? storedAfterSeconds();
    setBeforeSeconds(String(before));
    if (before > currentAfter) {
      setAfterSeconds(String(before));
      update('recording', { automaticClipBeforeSeconds: before, automaticClipAfterSeconds: before });
    } else {
      update('recording', { automaticClipBeforeSeconds: before });
    }
  }

  function commitAfter() {
    const after = parseSeconds(afterSeconds);
    if (after === null) {
      setAfterSeconds(String(storedAfterSeconds()));
      return;
    }
    const before = parseSeconds(beforeSeconds) ?? storedBeforeSeconds();
    const nextAfter = Math.max(after, before);
    setAfterSeconds(String(nextAfter));
    update('recording', { automaticClipAfterSeconds: nextAfter });
  }

  const afterMin = Math.max(1, parseSeconds(beforeSeconds) ?? storedBeforeSeconds());

  return (
    <div className="settings-page" data-page="buffer">
      <p className="settings-page-note">
        The replay buffer keeps recent footage available so an automatic highlight can start before
        the moment happens. It runs in Session + replay buffer and Replay buffer only modes.
      </p>

      <Field
        label="Buffer length"
        hint="How much recent footage is available for a live highlight, in seconds."
      >
        <TextField
          type="number"
          min={1}
          value={durationSeconds}
          onChange={setDurationSeconds}
          onBlur={commitDuration}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitDuration();
            }
          }}
          aria-label="Buffer length"
        />
      </Field>

      <Field
        label="Automatic highlights"
        hint={
          bufferModeOff
            ? recording.automaticClipsEnabled === true
              ? 'Inactive without a replay buffer recording mode. Turn this off to clear the saved setting, or change mode on the Recording tab.'
              : 'Needs a replay buffer recording mode (see the Recording tab).'
            : 'Saves a clip when Tript detects a positive moment in the game, using the seconds before and after below. You can always create them manually from a session in the player.'
        }
      >
        <Checkbox
          checked={recording.automaticClipsEnabled === true}
          disabled={bufferModeOff && recording.automaticClipsEnabled !== true}
          onChange={(enabled) => update('recording', { automaticClipsEnabled: enabled })}
        />
      </Field>

      <Field label="Seconds before each highlight" hint="How far before a detected highlight's start the clip begins.">
        <TextField
          type="number"
          min={1}
          value={beforeSeconds}
          onChange={setBeforeSeconds}
          onBlur={commitBefore}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitBefore();
            }
          }}
          disabled={bufferModeOff}
          aria-label="Seconds before each highlight"
        />
      </Field>

      <Field
        label="Seconds after each highlight"
        hint="How far after a detected highlight's end the clip continues. Cannot be less than the seconds before."
      >
        <TextField
          type="number"
          min={afterMin}
          value={afterSeconds}
          onChange={setAfterSeconds}
          onBlur={commitAfter}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              commitAfter();
            }
          }}
          disabled={bufferModeOff}
          aria-label="Seconds after each highlight"
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
