// SPDX-License-Identifier: GPL-2.0-or-later
//
// Which of the things the recorder can be doing it is actually doing. Pure, so each state is
// testable without a socket or a clock, and so the ordering between them is stated once rather than
// scattered through JSX.
//
// There is deliberately no "instant replay ready" state. The rolling buffer is not implemented —
// `RecordingModeExtensions.IsAlphaSupported` accepts Session only, and Buffer mode "writes nothing
// by design" — so a UI state for it would be showing the user something that cannot be true. Add it
// when the buffer exists, not before.

export type RecorderState =
  | { kind: 'disconnected' }
  | { kind: 'recording'; game: string | null; startedAt: number | null }
  | { kind: 'idle' };

export interface RecorderInputs {
  connection: 'connected' | 'connecting' | 'disconnected';
  recording: boolean;
  game: string | null;
  /** Unix seconds, from the state push. Null when the backend did not report one. */
  startedAt: number | null;
}

export function deriveRecorderState(inputs: RecorderInputs): RecorderState {
  // Ordered by what the user needs to know first. Disconnection wins because nothing else on the
  // bar can be acted on, and a stale "Recording" would be a lie the moment the socket dropped.
  if (inputs.connection !== 'connected') {
    return { kind: 'disconnected' };
  }
  if (inputs.recording) {
    return { kind: 'recording', game: inputs.game, startedAt: inputs.startedAt };
  }
  return { kind: 'idle' };
}

/**
 * `h:mm:ss` past an hour, `m:ss` below it. Sessions run for hours, so the hour field is the point;
 * padding it to `0:04:12` for a four-minute recording would be noise.
 */
export function formatElapsed(seconds: number): string {
  const whole = Math.max(0, Math.floor(seconds));
  const hours = Math.floor(whole / 3600);
  const minutes = Math.floor((whole % 3600) / 60);
  const secs = whole % 60;
  const pad = (value: number) => String(value).padStart(2, '0');
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(secs)}` : `${minutes}:${pad(secs)}`;
}
