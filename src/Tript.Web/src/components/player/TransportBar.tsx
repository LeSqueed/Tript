// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback transport row: play/pause, the current/total time readout, and the fullscreen
// toggle. Lives below the video in the non-fullscreen state (Modern YouTube model — controls are
// not overlaid on the video unless fullscreen).

import { formatTime } from './timelineModel';

export interface TransportBarProps {
  playing: boolean;
  currentTime: number;
  duration: number;
  onTogglePlayPause(): void;
  onToggleFullscreen(): void;
}

export function TransportBar({
  playing,
  currentTime,
  duration,
  onTogglePlayPause,
  onToggleFullscreen,
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
      <button type="button" className="btn ghost" onClick={onToggleFullscreen} aria-label="Toggle fullscreen">
        Fullscreen
      </button>
    </div>
  );
}
