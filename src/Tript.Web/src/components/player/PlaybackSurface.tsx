// SPDX-License-Identifier: GPL-2.0-or-later

import type { RefObject } from 'react';
import type { ContentItem } from '../../ipc/protocol';
import { contentUrl, thumbnailUrl } from '../../ipc/endpoints';

interface PlaybackSurfaceProps {
  item: ContentItem;
  videoRef: RefObject<HTMLVideoElement | null>;
  playing: boolean;
  hasStartedPlayback: boolean;
  onTogglePlayPause: () => void;
  onTimeUpdate: (time: number) => void;
  onDurationChange: (duration: number) => void;
  onPlay: () => void;
  onPause: () => void;
  onEnded: () => void;
  onError: () => void;
}

export function PlaybackSurface({
  item,
  videoRef,
  playing,
  hasStartedPlayback,
  onTogglePlayPause,
  onTimeUpdate,
  onDurationChange,
  onPlay,
  onPause,
  onEnded,
  onError,
}: PlaybackSurfaceProps) {
  return (
    <div className="video-frame">
      <video
        ref={videoRef}
        className="video-element"
        // No `controls`: the browser paints those over the picture. The transport row below the
        // video is the control surface. tabIndex keeps the element keyboard-reachable, which
        // `controls` used to provide — the overlay's focus trap matches it by tabindex.
        tabIndex={0}
        aria-label={`${item.title ?? item.fileName} — press space to play or pause`}
        src={contentUrl(item.filePath)}
        poster={thumbnailUrl(item.filePath)}
        autoPlay
        playsInline
        onClick={onTogglePlayPause}
        onTimeUpdate={(event) => onTimeUpdate(event.currentTarget.currentTime)}
        onDurationChange={(event) => onDurationChange(event.currentTarget.duration)}
        onPlay={onPlay}
        onPause={onPause}
        onEnded={onEnded}
        onError={onError}
      />
      {!hasStartedPlayback && !playing && (
        <button
          type="button"
          className="player-start-overlay"
          aria-label="Play recording"
          onClick={(event) => {
            event.stopPropagation();
            onTogglePlayPause();
          }}
        >
          <span className="player-start-icon" aria-hidden="true">&#9654;</span>
          <span>Play recording</span>
        </button>
      )}
    </div>
  );
}
