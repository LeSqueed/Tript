// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { examplePaths, readPlatformCapabilities, WINDOWS_CAPABILITIES } from './platformCapabilities';

describe('readPlatformCapabilities', () => {
  it('reads what a Linux host reports', () => {
    expect(readPlatformCapabilities({
      platform: 'linux',
      tray: false,
      startWithSystem: false,
      hideToTray: false,
      obsSharing: false,
      globalHotkeys: true,
      globalHotkeysNote: 'Only while an X11 window has focus.',
      notifications: true,
      notificationSounds: false,
    })).toEqual({
      platform: 'linux',
      tray: false,
      startWithSystem: false,
      hideToTray: false,
      obsSharing: false,
      globalHotkeys: true,
      globalHotkeysNote: 'Only while an X11 window has focus.',
      notifications: true,
      notificationSounds: false,
      globalHotkeysManagedByDesktop: false,
      globalHotkeysConfigurable: false,
      desktopHotkeyTriggers: { toggleRecording: null, manualBookmark: null, quickClip: null },
      screenChosenByDesktop: false,
      screenChoiceRemembered: false,
    });
  });

  it('ignores a message without a known platform', () => {
    expect(readPlatformCapabilities(undefined)).toBeUndefined();
    expect(readPlatformCapabilities('linux')).toBeUndefined();
    expect(readPlatformCapabilities({ tray: true })).toBeUndefined();
    expect(readPlatformCapabilities({ platform: 'amiga', tray: true })).toBeUndefined();
  });

  it('treats a missing or malformed feature as unavailable', () => {
    const capabilities = readPlatformCapabilities({ platform: 'linux', tray: 'yes', globalHotkeysNote: 7 });

    expect(capabilities?.tray).toBe(false);
    expect(capabilities?.obsSharing).toBe(false);
    expect(capabilities?.globalHotkeysNote).toBeNull();
  });

  it('assumes every Windows feature until the host says otherwise', () => {
    const { platform, tray, startWithSystem, hideToTray, obsSharing, globalHotkeys, notifications, notificationSounds } =
      WINDOWS_CAPABILITIES;
    expect(platform).toBe('windows');
    expect([tray, startWithSystem, hideToTray, obsSharing, globalHotkeys, notifications, notificationSounds])
      .not.toContain(false);
  });

  it('lets Windows record its own hotkeys rather than defer to a desktop', () => {
    expect(WINDOWS_CAPABILITIES.globalHotkeysManagedByDesktop).toBe(false);
  });

  it('reads the triggers the desktop assigned and drops empty ones', () => {
    const read = readPlatformCapabilities({
      platform: 'linux',
      globalHotkeys: true,
      globalHotkeysManagedByDesktop: true,
      globalHotkeysConfigurable: true,
      desktopHotkeyTriggers: { toggleRecording: 'Meta+F9', manualBookmark: '', quickClip: 7 },
    });

    expect(read?.globalHotkeysManagedByDesktop).toBe(true);
    expect(read?.globalHotkeysConfigurable).toBe(true);
    expect(read?.desktopHotkeyTriggers).toEqual({ toggleRecording: 'Meta+F9', manualBookmark: null, quickClip: null });
  });

  it('treats a host that sends no trigger information as not desktop-managed', () => {
    const read = readPlatformCapabilities({ platform: 'linux', globalHotkeys: true });

    expect(read?.globalHotkeysManagedByDesktop).toBe(false);
    expect(read?.desktopHotkeyTriggers).toEqual({ toggleRecording: null, manualBookmark: null, quickClip: null });
  });
});

describe('examplePaths', () => {
  it('suggests Windows paths on Windows', () => {
    const paths = examplePaths('windows');
    expect(paths.recordingFolder).toBe(String.raw`D:\Recordings`);
    expect(paths.trainingFolder).toBe(String.raw`C:\Users\You\Documents\Tript training`);
  });

  it('suggests home-directory paths on Linux', () => {
    expect(examplePaths('linux')).toEqual({
      recordingFolder: '/home/you/Videos/Tript',
      defaultRecordingFolder: 'Videos/Tript',
      trainingFolder: '/home/you/tript-training',
      gameExecutable: '/home/you/Games/Example/game',
    });
  });

  it('suggests macOS home paths on macOS', () => {
    expect(examplePaths('macos').gameExecutable).toBe('/Users/you/Games/Example/game');
  });

  it('shows Windows users a single-backslash game path', () => {
    expect(examplePaths('windows').gameExecutable).toBe('C:\\Games\\Example\\game.exe');
  });
});
