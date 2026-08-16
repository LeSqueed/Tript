// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback hook — the sync model.
//
// `currentTime` is the single source of truth for the playhead: the <video> element, both
// timelines and the transport all read and write it. The video is the driver — its `timeupdate`
// event is the only thing that advances `currentTime`; a timeline click or keyboard seek sets it
// directly and then writes `video.currentTime`. Because both timelines render from the same value,
// they can never disagree — that is the whole dual-sync contract.
//
// The contract only concerns seeking and the playhead position; the video element itself is owned
// by the PlayerView component (it must be, to render <video controls>), so the hook talks to it
// through a ref. `duration` starts at the session's fallback length and is replaced by the video's
// metadata when it arrives; if the video reports NaN (e.g. placeholder data with no media file)
// the fallback is kept.

import { useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';

/** The minimum seekable duration when no video metadata has arrived yet. */
const MIN_DURATION = 1;

export interface PlaybackState {
  /** The playhead position, seconds. The single source of truth for every timeline. */
  currentTime: number;
  /** The session length, seconds. Video metadata wins once it arrives. */
  duration: number;
  playing: boolean;
  /** Seek the playhead. Safe from any timeline; clamps to the duration. */
  seek(time: number): void;
  togglePlayPause(): void;
  /** React to the <video> element's own position changes (timeupdate). */
  onVideoTimeUpdate(time: number): void;
  /** React to the <video> element's metadata. */
  onVideoDuration(duration: number): void;
  /** The video element started playing (its own event, not the UI's request). */
  onVideoPlay(): void;
  /** The video element paused (its own event, not the UI's request). */
  onVideoPause(): void;
  onVideoEnded(): void;
  /** The <video> element ref, attached by the PlayerView. */
  videoRef: RefObject<HTMLVideoElement | null>;
}

export function usePlayback(itemKey: string, fallbackDuration: number): PlaybackState {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(fallbackDuration);
  const [playing, setPlaying] = useState(false);

  // A change of session resets the playhead — keyed on the item identity, not the duration, so
  // navigating between two sessions of the same length still resets. The fallback duration is the
  // item's declared length, replaced by video metadata when it arrives.
  useEffect(() => {
    setCurrentTime(0);
    setDuration(fallbackDuration);
    setPlaying(false);
    const video = videoRef.current;
    if (video) {
      video.currentTime = 0;
      video.pause();
    }
  }, [itemKey, fallbackDuration]);

  function seek(time: number): void {
    const target = Math.max(0, Math.min(time, duration));
    const video = videoRef.current;
    if (video) {
      video.currentTime = target;
    }
    setCurrentTime(target);
  }

  function togglePlayPause(): void {
    const video = videoRef.current;
    if (!video) {
      return;
    }
    if (video.paused) {
      void video.play().catch(() => {
        // Autoplay or codec restrictions — the UI reflects the element's state.
      });
    } else {
      video.pause();
    }
  }

  return {
    currentTime,
    duration,
    playing,
    seek,
    togglePlayPause,
    onVideoTimeUpdate(time: number): void {
      setCurrentTime(time);
    },
    onVideoDuration(videoDuration: number): void {
      if (Number.isFinite(videoDuration) && videoDuration > MIN_DURATION) {
        setDuration(videoDuration);
      }
    },
    onVideoPlay(): void {
      setPlaying(true);
    },
    onVideoPause(): void {
      setPlaying(false);
    },
    onVideoEnded(): void {
      setPlaying(false);
    },
    videoRef,
  };
}
