// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  DisplayFallbackWarning,
  DisplayInfo,
  DisplayResolution,
  SettingsMessageContent,
  SettingsModel,
} from './settingsModel';
import type { SettingsUpdateResultMessage } from '../ipc/protocol';
import { readAvailableDisplays, readDisplayFallbackWarning } from './displayModel';

export type SettingsPageName = 'recording' | 'buffer' | 'audio' | 'capture' | 'game' | 'general' | 'hotkeys';

const PAGE_KEY: Record<SettingsPageName, string> = {
  recording: 'recording',
  buffer: 'buffer',
  audio: 'audio',
  capture: 'capture',
  game: 'game',
  general: 'general',
  hotkeys: 'hotkeys',
};

const DEFAULT_SETTINGS: SettingsModel = {
  recording: {
    mode: 'SessionWithReplayBuffer',
    resolutionWidth: 1920,
    resolutionHeight: 1080,
    fps: 60,
    encoder: 'x264',
    quality: 10,
    rateControl: 'Cqp',
    bitrateKbps: 15000,
    maxBitrateKbps: 0,
    outputDirectory: null,
    trashRetentionHours: 24,
  },
  buffer: { enabled: false, duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 },
  audio: { outputMode: 'Normal', tracks: [], devices: [], mic: null, desktop: null },
  capture: { method: 'Auto', display: null, displayLabel: null },
  game: { gameCaptureTimeout: 10, gameList: [], autoRecordDetectedGames: true, ignoredApplications: [] },
  general: {
    startWithWindows: false,
    startupVisibility: 'Window',
    minimizeBehavior: 'Taskbar',
    closeBehavior: 'Exit',
    checkForUpdatesAutomatically: true,
    notifications: {
      enabled: true,
      recordingStarted: true,
      recordingStartedSound: true,
      recordingStopped: true,
      recordingStoppedSound: true,
      errors: true,
      errorsSound: true,
    },
  },
  hotkeys: {
    enabled: true,
    toggleRecording: { modifiers: ['Control'], key: 'F9' },
    manualBookmark: { modifiers: ['Control'], key: 'F10' },
    quickClip: { modifiers: ['Control'], key: 'F7' },
    quickClipSeconds: 30,
  },
};

export interface SettingsController {
  settings: SettingsModel;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => string;
  settingsUpdateResult: SettingsUpdateResultMessage | null;
  hasSettings: boolean;
  lastCause?: string;
  externalPushCount: number;
  availableEncoders?: string[];
  displayResolution?: DisplayResolution;
  availableDisplays: DisplayInfo[] | null;
  displayFallbackWarning: DisplayFallbackWarning | null;
  appVersion?: string | null;
}

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
  const [appVersion, setAppVersion] = useState<string | null | undefined>(undefined);
  const [settingsUpdateResult, setSettingsUpdateResult] = useState<SettingsUpdateResultMessage | null>(null);
  const pendingCauses = useRef(new Set<string>());

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
       setSettings(mergeSettings(pushed));
      setAvailableEncoders(
        Array.isArray(message.availableEncoders)
          ? message.availableEncoders.filter((id): id is string => typeof id === 'string' && id !== '')
          : undefined,
      );
      setDisplayResolution(readDisplayResolution(message.displayResolution));
      setAvailableDisplays(readAvailableDisplays(message.availableDisplays));
      setDisplayFallbackWarning(readDisplayFallbackWarning(message.displayFallbackWarning));
      setAppVersion(typeof message.appVersion === 'string' ? message.appVersion : null);
      setHasSettings(true);
      if (!isSelfEcho) {
        setExternalPushCount((count) => count + 1);
      }
    });

    return unsubscribe;
  }, [client]);

  useEffect(() => {
    // SettingsView (and this hook with it) is mounted up front, often before the socket has
    // finished connecting — a send() issued before then is silently dropped (no queueing), so
    // request once immediately if already connected, and again on every future connect/reconnect.
    if (client.state === 'connected') {
      client.send('ListSettings');
    }
    return client.onStateChange((state) => {
      if (state === 'connected') {
        client.send('ListSettings');
      }
    });
  }, [client]);

  useEffect(() => client.on('settingsUpdateResult', (content) => {
    const result = content as Partial<SettingsUpdateResultMessage> | null;
    if (typeof result?.requestId === 'string' && typeof result.success === 'boolean') {
      pendingCauses.current.delete(result.requestId);
      setSettingsUpdateResult({
        requestId: result.requestId,
        success: result.success,
        error: typeof result.error === 'string' ? result.error : null,
      });
    }
  }), [client]);

  useEffect(() => client.onStateChange((state) => {
    if (state === 'connected' || pendingCauses.current.size === 0) {
      return;
    }

    const requestId = Array.from(pendingCauses.current).at(-1)!;
    pendingCauses.current.clear();
    setSettingsUpdateResult({
      requestId,
      success: false,
      error: 'The settings update was interrupted by a lost connection.',
    });
  }), [client]);

  const update = useMemo(
    () => (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => {
      const cause = `tript:${page}:${crypto.randomUUID()}`;
      if (client.state !== 'connected') {
        setSettingsUpdateResult({
          requestId: cause,
          success: false,
          error: 'Connect to Tript before saving settings.',
        });
        return cause;
      }
      pendingCauses.current.add(cause);
      client.send('UpdateSettings', { requestId: cause, settings: { [PAGE_KEY[page]]: patch } });
      return cause;
    },
    [client],
  );

  return {
    settings,
    update,
    settingsUpdateResult,
    hasSettings,
    lastCause,
    externalPushCount,
    availableEncoders,
    displayResolution,
    availableDisplays,
    displayFallbackWarning,
    appVersion,
  };
}

function mergeSettings(pushed: SettingsModel): SettingsModel {
  return {
    ...DEFAULT_SETTINGS,
    ...pushed,
    recording: { ...DEFAULT_SETTINGS.recording, ...pushed.recording },
    buffer: { ...DEFAULT_SETTINGS.buffer, ...pushed.buffer },
    audio: { ...DEFAULT_SETTINGS.audio, ...pushed.audio },
    capture: { ...DEFAULT_SETTINGS.capture, ...pushed.capture },
    game: { ...DEFAULT_SETTINGS.game, ...pushed.game },
    general: {
      ...DEFAULT_SETTINGS.general,
      ...pushed.general,
      notifications: {
        ...DEFAULT_SETTINGS.general.notifications,
        ...pushed.general?.notifications,
      },
    },
    hotkeys: {
      ...DEFAULT_SETTINGS.hotkeys,
      ...pushed.hotkeys,
      toggleRecording: { ...DEFAULT_SETTINGS.hotkeys.toggleRecording, ...pushed.hotkeys?.toggleRecording },
      manualBookmark: { ...DEFAULT_SETTINGS.hotkeys.manualBookmark, ...pushed.hotkeys?.manualBookmark },
      quickClip: { ...DEFAULT_SETTINGS.hotkeys.quickClip, ...pushed.hotkeys?.quickClip },
    },
  };
}
