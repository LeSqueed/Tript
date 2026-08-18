// SPDX-License-Identifier: GPL-2.0-or-later
//
// The settings model hook: the single place the settings pages read from the `settings` message and
// write via `UpdateSettings`. The backend is the source of truth.

import { useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  DisplayFallbackWarning,
  DisplayInfo,
  DisplayResolution,
  SettingsMessageContent,
  SettingsModel,
} from './settingsModel';
import { readAvailableDisplays, readDisplayFallbackWarning } from './displayModel';

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
  recording: {
    mode: 'Hybrid',
    resolutionWidth: 1920,
    resolutionHeight: 1080,
    fps: 60,
    encoder: 'x264',
    quality: 10,
    // The backend's own defaults (Tript.Settings/SettingPages.cs): constant quality, and a bitrate
    // that only the rate-targeted modes read.
    rateControl: 'Cqp',
    bitrateKbps: 15000,
    maxBitrateKbps: 0,
    outputDirectory: null,
  },
  buffer: { enabled: false, duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 },
  audio: { outputMode: 'Normal', tracks: [], devices: [], mic: null, desktop: null },
  capture: { method: 'Auto', display: null, displayLabel: null },
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
  /**
   * The H.264 encoder ids this machine supports, from the settings push. Undefined means "unknown"
   * (an older backend, or a host that cannot probe the encoder registry and sends null) — the
   * recording page falls back rather than showing an empty selector.
   */
  availableEncoders?: string[];
  /**
   * The primary display's pixel size, from the settings push. Undefined means "unknown" (an older
   * backend, or a host whose display detection failed and sends null) — the resolution selector then
   * offers its presets alone rather than an option built from a garbage size.
   */
  displayResolution?: DisplayResolution;
  /**
   * The monitors attached right now, from the settings push. `null` is "the host could not
   * enumerate" and an empty array is "enumerated, none found" — the capture page renders those two
   * differently, so they are not collapsed here.
   */
  availableDisplays: DisplayInfo[] | null;
  /** Set while the recorder is falling back off the preferred monitor, else null. */
  displayFallbackWarning: DisplayFallbackWarning | null;
}

/**
 * The display resolution from a push, or undefined when it is not a usable pair of pixel counts.
 * Validated rather than trusted because it ends up in a `<select>` option the user can pick and the
 * backend then records at: a zero, a negative, a non-integer or a non-number is not a resolution,
 * and offering it would be worse than offering nothing.
 */
function readDisplayResolution(value: unknown): DisplayResolution | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const { width, height } = value as { width?: unknown; height?: unknown };
  if (typeof width !== 'number' || typeof height !== 'number') {
    return undefined;
  }
  if (!Number.isInteger(width) || !Number.isInteger(height) || width <= 0 || height <= 0) {
    return undefined;
  }
  return { width, height };
}

export function useSettings(client: IpcClient): SettingsController {
  const [settings, setSettings] = useState<SettingsModel>(DEFAULT_SETTINGS);
  const [hasSettings, setHasSettings] = useState(false);
  const [lastCause, setLastCause] = useState<string | undefined>(undefined);
  const [externalPushCount, setExternalPushCount] = useState(0);
  const [availableEncoders, setAvailableEncoders] = useState<string[] | undefined>(undefined);
  const [displayResolution, setDisplayResolution] = useState<DisplayResolution | undefined>(undefined);
  const [availableDisplays, setAvailableDisplays] = useState<DisplayInfo[] | null>(null);
  const [displayFallbackWarning, setDisplayFallbackWarning] = useState<DisplayFallbackWarning | null>(null);
  const pendingCauses = useRef(new Set<string>());
  const causeSerial = useRef(0);

  useEffect(() => {
    const unsubscribe = client.on('settings', (content) => {
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
      // The encoder list is a sibling of `settings` on the wire, not a field inside it: it is what
      // this machine's runtime registered, not a persisted setting. Anything that is not an array
      // of ids (absent, null, or a shape we do not recognise) is "unknown", and the recording page
      // falls back rather than offering an empty selector.
      setAvailableEncoders(
        Array.isArray(message.availableEncoders)
          ? message.availableEncoders.filter((id): id is string => typeof id === 'string' && id !== '')
          : undefined,
      );
      // Same reasoning for the display size: a sibling of `settings`, because it is what this
      // machine's primary display measures rather than something the user configured.
      setDisplayResolution(readDisplayResolution(message.displayResolution));
      // Siblings again, and for the same reason: the monitor list and the fallback the recorder made
      // are facts about this machine, not settings, so they are never sent back.
      setAvailableDisplays(readAvailableDisplays(message.availableDisplays));
      setDisplayFallbackWarning(readDisplayFallbackWarning(message.displayFallbackWarning));
      setHasSettings(true);
      if (!isSelfEcho) {
        setExternalPushCount((count) => count + 1);
      }
    });

    // Ask for a push now that the handler is installed. The backend's connect-time push
    // (NewConnection) is broadcast when the socket opens, and the settings route mounts on demand —
    // usually long after — so this hook would otherwise sit on DEFAULT_SETTINGS with
    // `availableEncoders` unknown until the user's first edit provoked a push.
    client.send('ListSettings');

    return unsubscribe;
  }, [client]);

  const update = useMemo(
    () => (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => {
      const cause = `tript:${page}:${++causeSerial.current}`;
      pendingCauses.current.add(cause);
      client.send('UpdateSettings', { settings: { [PAGE_KEY[page]]: patch } });
    },
    [client],
  );

  return {
    settings,
    update,
    hasSettings,
    lastCause,
    externalPushCount,
    availableEncoders,
    displayResolution,
    availableDisplays,
    displayFallbackWarning,
  };
}
