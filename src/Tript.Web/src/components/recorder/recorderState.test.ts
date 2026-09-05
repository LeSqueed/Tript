// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { deriveRecorderState, formatElapsed } from './recorderState';

const inputs = (overrides: Partial<Parameters<typeof deriveRecorderState>[0]> = {}) => ({
  connection: 'connected' as const,
  recording: false,
  game: null,
  detected: false,
  startedAt: null,
  activeRecordingMode: null,
  ...overrides,
});

describe('deriveRecorderState', () => {
  it('reports disconnection above everything else', () => {
    // A stale "Recording" would be a lie the moment the socket dropped, and nothing else on the bar
    // can be acted on anyway.
    expect(
      deriveRecorderState(inputs({ connection: 'disconnected', recording: true, game: 'Overwatch' })),
    ).toEqual({ kind: 'disconnected' });
  });

  it('treats connecting as not connected', () => {
    expect(deriveRecorderState(inputs({ connection: 'connecting' })).kind).toBe('disconnected');
  });

  it('reports recording with its game and start time', () => {
    expect(
      deriveRecorderState(inputs({ recording: true, game: 'Overwatch', startedAt: 1_700_000_000 })),
    ).toEqual({ kind: 'recording', game: 'Overwatch', startedAt: 1_700_000_000 });
  });

  it('stays neutral when configured auto-detection is idle', () => {
    expect(deriveRecorderState(inputs())).toEqual({ kind: 'idle' });
  });

  it('reports buffer-only activity distinctly while preserving its game and start time', () => {
    expect(deriveRecorderState(inputs({
      recording: true,
      activeRecordingMode: 'ReplayBufferOnly',
      game: 'Overwatch',
      startedAt: 1_700_000_000,
    }))).toEqual({ kind: 'buffering', game: 'Overwatch', startedAt: 1_700_000_000 });
  });

  it('reports a running detected game while recording is idle', () => {
    expect(deriveRecorderState(inputs({ game: 'Counter-Strike 2', detected: true }))).toEqual({
      kind: 'detected',
      game: 'Counter-Strike 2',
    });
  });

  it('does not claim an undetected game is running', () => {
    expect(deriveRecorderState(inputs({ game: 'Counter-Strike 2' }))).toEqual({ kind: 'idle' });
  });

});

describe('formatElapsed', () => {
  it('drops the hour field below an hour', () => {
    expect(formatElapsed(0)).toBe('0:00');
    expect(formatElapsed(65)).toBe('1:05');
    expect(formatElapsed(3599)).toBe('59:59');
  });

  it('shows hours for the long sessions this exists for', () => {
    expect(formatElapsed(3600)).toBe('1:00:00');
    expect(formatElapsed(5025)).toBe('1:23:45');
    expect(formatElapsed(8 * 3600 + 61)).toBe('8:01:01');
  });

  it('never renders a negative clock', () => {
    expect(formatElapsed(-5)).toBe('0:00');
  });
});
