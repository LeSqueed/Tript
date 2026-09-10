// SPDX-License-Identifier: GPL-2.0-or-later

import type { RecordingMode } from '../../settings/settingsModel';

export type RecorderState =
  | { kind: 'disconnected' }
  | { kind: 'recording'; game: string | null; startedAt: number | null }
  | { kind: 'buffering'; game: string | null; startedAt: number | null }
  | { kind: 'detected'; game: string }
  | { kind: 'idle' };

export interface RecorderInputs {
  connection: 'connected' | 'connecting' | 'disconnected';
  recording: boolean;
  game: string | null;
  detected: boolean;
  startedAt: number | null;
  activeRecordingMode: RecordingMode | null;
}

export function deriveRecorderState(inputs: RecorderInputs): RecorderState {
  if (inputs.connection !== 'connected') {
    return { kind: 'disconnected' };
  }
  if (inputs.recording) {
    return {
      kind: inputs.activeRecordingMode === 'ReplayBufferOnly' ? 'buffering' : 'recording',
      game: inputs.game,
      startedAt: inputs.startedAt,
    };
  }
  if (inputs.detected && inputs.game) {
    return { kind: 'detected', game: inputs.game };
  }
  return { kind: 'idle' };
}

export function formatElapsed(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const secs = whole % 60;
  const pad = (value: number) => String(value).padStart(2, '0');
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(secs)}` : `${minutes}:${pad(secs)}`;
}
