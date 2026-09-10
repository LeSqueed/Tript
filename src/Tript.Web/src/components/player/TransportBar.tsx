// SPDX-License-Identifier: GPL-2.0-or-later

import { Button, SelectField, Slider } from '../ui/controls';
import { formatTime } from './timelineModel';

export const PLAYBACK_RATES = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2] as const;

const RATE_OPTIONS = PLAYBACK_RATES.map((rate) => ({ value: String(rate), label: `${rate}×` }));

export interface TransportBarProps {
  playing: boolean;
  currentTime: number;
  duration: number;
  volume: number;
  muted: boolean;
  playbackRate: number;
  onTogglePlayPause(): void;
  onToggleFullscreen(): void;
  onVolumeChange(volume: number): void;
  onToggleMute(): void;
  onPlaybackRateChange(rate: number): void;
  onPrevious?(): void;
  onNext?(): void;
  canNavigatePrevious?: boolean;
  canNavigateNext?: boolean;
  itemPosition?: { current: number; total: number };
  favorite?: boolean;
  onToggleFavorite?(): void;
  onDelete?(): void;
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
  canNavigatePrevious = false,
  canNavigateNext = false,
  itemPosition,
  favorite = false,
  onToggleFavorite,
  onDelete,
}: TransportBarProps) {
  return (
    <div className="transport-bar">
      <div className="transport-primary">
        {onPrevious && (
        <Button
          variant="ghost"
          size="icon"
          icon="chevronLeft"
          onClick={onPrevious}
          disabled={!canNavigatePrevious}
          aria-label="Previous item"
          title="Previous item (Shift+Left)"
        />
        )}
        <Button
        variant="ghost"
        size="icon"
        icon={playing ? 'pause' : 'play'}
        onClick={onTogglePlayPause}
        aria-label={playing ? 'Pause' : 'Play'}
        />
        {onNext && (
        <Button
          variant="ghost"
          size="icon"
          icon="chevronRight"
          onClick={onNext}
          disabled={!canNavigateNext}
          aria-label="Next item"
          title="Next item (Shift+Right)"
        />
        )}
        {itemPosition && (
        <span className="transport-position" aria-label={`Item ${itemPosition.current} of ${itemPosition.total}`}>
          {itemPosition.current} of {itemPosition.total}
        </span>
        )}
        {onToggleFavorite && (
        <Button
          variant="ghost"
          size="icon"
          icon="star"
          iconFilled={favorite}
          active={favorite}
          onClick={onToggleFavorite}
          aria-label={favorite ? 'Remove from favorites' : 'Add to favorites'}
          aria-pressed={favorite}
          title={`${favorite ? 'Remove from' : 'Add to'} favorites (F)`}
        />
        )}
        {onDelete && (
          <Button
            variant="ghost"
            size="icon"
            icon="trash"
            onClick={onDelete}
            aria-label="Move to trash"
            title="Move to trash (Delete)"
          />
        )}
        <span className="transport-time">
        <span data-testid="transport-current">{formatTime(currentTime)}</span>
        <span className="transport-sep"> / </span>
        <span data-testid="transport-duration">{formatTime(duration)}</span>
        </span>
      </div>
      <div className="transport-secondary">
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
    </div>
  );
}
