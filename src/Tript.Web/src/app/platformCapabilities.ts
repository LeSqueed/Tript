// SPDX-License-Identifier: GPL-2.0-or-later

import { createContext, useContext, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import { useIpcMessage } from './useConnection';

export type PlatformName = 'windows' | 'linux' | 'macos';

export interface PlatformCapabilities {
  platform: PlatformName;
  tray: boolean;
  startWithSystem: boolean;
  hideToTray: boolean;
  obsSharing: boolean;
  globalHotkeys: boolean;
  globalHotkeysNote: string | null;
  notifications: boolean;
  notificationSounds: boolean;
  globalHotkeysManagedByDesktop: boolean;
  globalHotkeysConfigurable: boolean;
  desktopHotkeyTriggers: DesktopHotkeyTriggers;
  screenChosenByDesktop: boolean;
  screenChoiceRemembered: boolean;
}

export interface DesktopHotkeyTriggers {
  toggleRecording: string | null;
  manualBookmark: string | null;
  quickClip: string | null;
}

const NO_DESKTOP_TRIGGERS: DesktopHotkeyTriggers = { toggleRecording: null, manualBookmark: null, quickClip: null };

export const WINDOWS_CAPABILITIES: PlatformCapabilities = {
  platform: 'windows',
  tray: true,
  startWithSystem: true,
  hideToTray: true,
  obsSharing: true,
  globalHotkeys: true,
  globalHotkeysNote: null,
  notifications: true,
  notificationSounds: true,
  globalHotkeysManagedByDesktop: false,
  globalHotkeysConfigurable: false,
  desktopHotkeyTriggers: NO_DESKTOP_TRIGGERS,
  screenChosenByDesktop: false,
  screenChoiceRemembered: false,
};

const PLATFORMS: readonly PlatformName[] = ['windows', 'linux', 'macos'];

const FLAGS = [
  'tray',
  'startWithSystem',
  'hideToTray',
  'obsSharing',
  'globalHotkeys',
  'notifications',
  'notificationSounds',
  'globalHotkeysManagedByDesktop',
  'globalHotkeysConfigurable',
  'screenChosenByDesktop',
  'screenChoiceRemembered',
] as const;

function readTrigger(value: unknown): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

function readDesktopTriggers(value: unknown): DesktopHotkeyTriggers {
  if (!value || typeof value !== 'object') {
    return NO_DESKTOP_TRIGGERS;
  }
  const wire = value as Record<string, unknown>;
  return {
    toggleRecording: readTrigger(wire.toggleRecording),
    manualBookmark: readTrigger(wire.manualBookmark),
    quickClip: readTrigger(wire.quickClip),
  };
}

export function readPlatformCapabilities(value: unknown): PlatformCapabilities | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const wire = value as Record<string, unknown>;
  if (typeof wire.platform !== 'string' || !PLATFORMS.includes(wire.platform as PlatformName)) {
    return undefined;
  }
  const capabilities: PlatformCapabilities = {
    ...WINDOWS_CAPABILITIES,
    platform: wire.platform as PlatformName,
    globalHotkeysNote: typeof wire.globalHotkeysNote === 'string' ? wire.globalHotkeysNote : null,
    desktopHotkeyTriggers: readDesktopTriggers(wire.desktopHotkeyTriggers),
  };
  for (const flag of FLAGS) {
    capabilities[flag] = wire[flag] === true;
  }
  return capabilities;
}

export const PlatformCapabilitiesContext = createContext<PlatformCapabilities>(WINDOWS_CAPABILITIES);

export function usePlatformCapabilities(): PlatformCapabilities {
  return useContext(PlatformCapabilitiesContext);
}

export function useHostPlatformCapabilities(client: IpcClient): PlatformCapabilities {
  const [capabilities, setCapabilities] = useState<PlatformCapabilities>(WINDOWS_CAPABILITIES);

  useIpcMessage(client, 'settings', (content) => {
    const pushed = readPlatformCapabilities((content as { platformCapabilities?: unknown } | null)?.platformCapabilities);
    if (pushed) {
      setCapabilities(pushed);
    }
  });

  return capabilities;
}

export interface ExamplePaths {
  recordingFolder: string;
  defaultRecordingFolder: string;
  trainingFolder: string;
  gameExecutable: string;
}

export function examplePaths(platform: PlatformName): ExamplePaths {
  if (platform === 'windows') {
    return {
      recordingFolder: String.raw`D:\Recordings`,
      defaultRecordingFolder: String.raw`Videos\Tript`,
      trainingFolder: String.raw`C:\Users\You\Documents\Tript training`,
      gameExecutable: String.raw`C:\Games\Example\game.exe`,
    };
  }
  const home = platform === 'macos' ? '/Users/you' : '/home/you';
  return {
    recordingFolder: `${home}/Videos/Tript`,
    defaultRecordingFolder: 'Videos/Tript',
    trainingFolder: `${home}/tript-training`,
    gameExecutable: `${home}/Games/Example/game`,
  };
}
