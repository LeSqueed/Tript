// SPDX-License-Identifier: GPL-2.0-or-later
//
// The settings model hook: the single place the settings pages read from the `settings` message
// and write via `UpdateSettings`.
//
// The backend is the source of truth. Every change is sent as a **partial** settings object
// scoped to what the user edited (one page at a time, never the whole object) and tagged with a
// `cause` so the backend's echo can be told apart from a change that originated elsewhere.
//
// Cause-echo discipline: every settings push is applied to the model — a push echoing our own
// cause is the backend's acceptance of our change and carries the newly-persisted values (e.g.
// the track list after "add track"), so it must render. What the cause is used for is the
// opposite danger: a push must not clobber a free-text edit the user is still typing. Pages hold
// local drafts for free-text inputs and re-sync them from the model only when `externalPushCount`
// changes — i.e. when a push was *not* the echo of their own edit. A push with no cause, or a
// cause we did not send, is a real external change and increments that counter.
//
// The page object sent on the wire is shaped `{ recording: {...} }` / `{ audio: {...} }` etc. —
// the backend's Settings model nests each page under its camelCase property (spec/frontend.md,
// the settings-page structure; Tript.Settings/Settings.cs). An update carries that whole nested
// page object, never the top-level settings object.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { SettingsMessageContent, SettingsModel } from './settingsModel';

export type SettingsPageName = 'recording' | 'buffer' | 'audio' | 'capture' | 'game';

/** The page key on the wire, used both to read and to send. */
const PAGE_KEY: Record<SettingsPageName, string> = {
  recording: 'recording',
  buffer: 'buffer',
  audio: 'audio',
  capture: 'capture',
  game: 'game',
};

/** The default settings object, so the pages render even before the first push. */
const DEFAULT_SETTINGS: SettingsModel = {
  recording: { mode: 'Hybrid', resolutionWidth: 1920, resolutionHeight: 1080, fps: 60, encoder: 'x264', quality: 10 },
  buffer: { enabled: false, duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 },
  audio: { outputMode: 'Normal', tracks: [], devices: [], mic: null, desktop: null },
  capture: { method: 'Auto', display: null },
  game: { captureMode: 'Auto', gameCaptureTimeout: 10, gameList: [] },
};

export interface SettingsController {
  settings: SettingsModel;
  /** Send a page-scoped partial update. The whole page object is sent, never the full settings. */
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  /** True when the backend has pushed at least one settings message. */
  hasSettings: boolean;
  /** The cause of the most recent settings push, if any. */
  lastCause?: string;
  /**
   * Increments on every settings push that was not the echo of our own edit. Pages with
   * free-text drafts re-sync them from the model on this change, never on their own echo.
   */
  externalPushCount: number;
}

export function useSettings(client: IpcClient): SettingsController {
  const [settings, setSettings] = useState<SettingsModel>(DEFAULT_SETTINGS);
  const [hasSettings, setHasSettings] = useState(false);
  const [lastCause, setLastCause] = useState<string | undefined>(undefined);
  const [externalPushCount, setExternalPushCount] = useState(0);
  const pendingCauses = useRef(new Set<string>());
  const causeSerial = useRef(0);

  useEffect(() => {
    return client.on('settings', (content) => {
      const message = content as SettingsMessageContent;
      const pushed = message?.settings;
      if (!pushed || typeof pushed !== 'object') {
        return;
      }
      const cause = message.cause;
      const isSelfEcho = cause !== undefined && pendingCauses.current.has(cause);
      if (isSelfEcho) {
        pendingCauses.current.delete(cause);
      }
      setLastCause(cause);
      setSettings(pushed);
      setHasSettings(true);
      if (!isSelfEcho) {
        setExternalPushCount((count) => count + 1);
      }
    });
  }, [client]);

  const update = useMemo(
    () => (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => {
      const cause = `tript:${page}:${++causeSerial.current}`;
      pendingCauses.current.add(cause);
      client.send('UpdateSettings', { settings: { [PAGE_KEY[page]]: patch } });
    },
    [client],
  );

  return { settings, update, hasSettings, lastCause, externalPushCount };
}
