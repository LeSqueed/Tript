// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback transport row: play/pause, the timecode, speed, volume and fullscreen. This is the
// player's entire control surface — the video carries no `controls` attribute, which would paint
// browser chrome over the picture. Controls sit below the video, never on it.
//
// Icon-led rather than worded: a row of text buttons reads as a toolbar, not a player. Every icon
// button keeps its aria-label, so nothing is lost to assistive tech.

import { Button, SelectField, Slider } from '../ui/controls';
import { formatTime } from './timelineModel';

/**
 * Review speeds, either side of normal. Slow matters as much as fast here: a fight worth clipping is
 * often decided in a second, and 0.25× is what makes it readable.
 */
export const PLAYBACK_RATES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2] as const;

const RATE_OPTIONS = PLAYBACK_RATES.map((rate) => ({ value: String(rate), label: `${rate}×` }));

export interface TransportBarProps {
  playing: boolean;
  currentTime: number;
  duration: number;
  /** 0..1. Shown as zero while muted; restoring a level on unmute is the caller's job. */
  volume: number;
  muted: boolean;
  playbackRate: number;
  onTogglePlayPause(): void;
  onToggleFullscreen(): void;
  onVolumeChange(volume: number): void;
  onToggleMute(): void;
  onPlaybackRateChange(rate: number): void;
  /** Session navigation. Absent when there is only one thing to play. */
  onPrevious?(): void;
  onNext?(): void;
  canNavigate?: boolean;
}

export function TransportBar({
  playing,
  currentTime,
  duration,
  volume,
  muted,
  playbackRate,
  onTogglePlayPause,
  onToggleFullscreen,
  onVolumeChange,
  onToggleMute,
  onPlaybackRateChange,
  onPrevious,
  onNext,
  canNavigate = false,
}: TransportBarProps) {
  return (
    <div className="transport-bar">
      {onPrevious && (
        <Button
          variant="ghost"
          size="icon"
          icon="chevronLeft"
          onClick={onPrevious}
          disabled={!canNavigate}
          aria-label="Previous recording"
        />
      )}
      <Button
        variant="ghost"
        size="icon"
        icon={playing ? 'pause' : 'play'}
        onClick={onTogglePlayPause}
        // The name says what pressing it will do, the way native controls do — and it is the only
        // thing carrying that state now the button has no text.
        aria-label={playing ? 'Pause' : 'Play'}
      />
      {onNext && (
        <Button
          variant="ghost"
          size="icon"
          icon="chevronRight"
          onClick={onNext}
          disabled={!canNavigate}
          aria-label="Next recording"
        />
      )}
      <span className="transport-time">
        <span data-testid="transport-current">{formatTime(currentTime)}</span>
        <span className="transport-sep"> / </span>
        <span data-testid="transport-duration">{formatTime(duration)}</span>
      </span>

      <span className="transport-spacer" />

      <SelectField
        compact
        aria-label="Playback speed"
        value={String(playbackRate)}
        options={RATE_OPTIONS}
        onChange={(value) => onPlaybackRateChange(Number(value))}
      />
      <div className="transport-volume">
        <Button
          variant="ghost"
          size="icon"
          icon={muted ? 'volumeOff' : 'volume'}
          onClick={onToggleMute}
          aria-label={muted ? 'Unmute' : 'Mute'}
        />
        <Slider aria-label="Volume" value={muted ? 0 : volume} onChange={onVolumeChange} />
      </div>
      <Button
        variant="ghost"
        size="icon"
        icon="fullscreen"
        onClick={onToggleFullscreen}
        aria-label="Toggle fullscreen"
      />
    </div>
  );
}
