// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback transport row: play/pause, the current/total time readout, volume, and the
// fullscreen toggle. This is the player's entire control surface — the video element deliberately
// does not carry the native `controls` attribute, which would paint browser chrome over the
// picture. Controls sit below the video, never on it.

import { formatTime } from './timelineModel';

export interface TransportBarProps {
  playing: boolean;
  currentTime: number;
  duration: number;
  /** 0..1. Shown as zero while muted; restoring a level on unmute is the caller's job. */
  volume: number;
  muted: boolean;
  onTogglePlayPause(): void;
  onToggleFullscreen(): void;
  onVolumeChange(volume: number): void;
  onToggleMute(): void;
}

export function TransportBar({
  playing,
  currentTime,
  duration,
  volume,
  muted,
  onTogglePlayPause,
  onToggleFullscreen,
  onVolumeChange,
  onToggleMute,
}: TransportBarProps) {
  return (
    <div className="transport-bar">
      <button type="button" className="btn ghost" onClick={onTogglePlayPause} aria-label="Play or pause">
        {playing ? 'Pause' : 'Play'}
      </button>
      <span className="transport-time">
        <span data-testid="transport-current">{formatTime(currentTime)}</span>
        <span className="transport-sep"> / </span>
        <span data-testid="transport-duration">{formatTime(duration)}</span>
      </span>
      <span className="transport-spacer" />
      <div className="transport-volume">
        <button
          type="button"
          className="btn ghost small"
          onClick={onToggleMute}
          aria-label={muted ? 'Unmute' : 'Mute'}
        >
          {muted ? 'Unmute' : 'Mute'}
        </button>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          // Muted reads as zero. Touching the slider then reports the displayed position, so the
          // caller is what remembers where to come back to.
          value={muted ? 0 : volume}
          aria-label="Volume"
          onChange={(event) => onVolumeChange(Number(event.currentTarget.value))}
        />
      </div>
      <button type="button" className="btn ghost" onClick={onToggleFullscreen} aria-label="Toggle fullscreen">
        Fullscreen
      </button>
    </div>
  );
}
