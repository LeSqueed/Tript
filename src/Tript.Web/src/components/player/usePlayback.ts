// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback hook — the sync model. `currentTime` is the single source of truth for the playhead:
// the <video> element, both timelines and the transport all read and write it.

import { useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';

export interface PlaybackState {
  /** The playhead position, seconds. The single source of truth for every timeline. */
  currentTime: number;
  /**
   * The session length, seconds — the media's own duration once known, the caller's fallback until
   * then. Good enough to lay out a timeline; use it as a *bound* only together with `durationKnown`.
   */
  duration: number;
  /** Whether `duration` came from the media element rather than from the caller's fallback. */
  durationKnown: boolean;
  playing: boolean;
  /** Seek the playhead. Safe from any timeline; clamps to the duration. */
  seek(time: number): void;
  togglePlayPause(): void;
  /** Preserve the current play/pause intent across the next item-key change. */
  prepareItemChange(shouldPlay: boolean): void;
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
  // The media's own duration, or null while nothing has been measured. Kept separate from the
  // fallback rather than seeded with it, so that (a) "measured" is distinguishable from "guessed" and
  // (b) a later change of the fallback (a `content` push filling in the metadata length) cannot
  // overwrite a duration the media already reported.
  const [mediaDuration, setMediaDuration] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  const nextItemShouldPlay = useRef<boolean | null>(null);
  const duration = mediaDuration ?? Math.max(0, fallbackDuration);
  const durationKnown = mediaDuration !== null;

  // A change of session resets the playhead — keyed on the item identity, not the duration, so
  // navigating between two sessions of the same length still resets, and a metadata update for the
  // session being watched does not yank the playhead back to 0.
  useEffect(() => {
    setCurrentTime(0);
    setMediaDuration(null);
    const shouldPlay = nextItemShouldPlay.current ?? true;
    nextItemShouldPlay.current = null;
    setPlaying(false);
    const video = videoRef.current;
    if (video) {
      video.currentTime = 0;
      video.pause();
      if (shouldPlay) {
        void video.play().catch(() => {
          // Browser autoplay policies may require the existing play overlay to be clicked.
        });
      }
    }
  }, [itemKey]);

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
    durationKnown,
    playing,
    seek,
    togglePlayPause,
    prepareItemChange(shouldPlay: boolean): void {
      nextItemShouldPlay.current = shouldPlay;
    },
    onVideoTimeUpdate(time: number): void {
      setCurrentTime(time);
    },
    onVideoDuration(videoDuration: number): void {
      // Any finite positive duration counts: the element reports NaN before its metadata is loaded
      // and Infinity for an open-ended stream, and both must stay "unknown", but a genuinely short
      // recording is a measurement like any other. (This used to require > 1s, which left a 0.8s file
      // bounded by the 120s placeholder.)
      if (!Number.isFinite(videoDuration) || videoDuration <= 0) {
        return;
      }
      setMediaDuration(videoDuration);
      // The media turned out to be shorter than the fallback suggested: the playhead cannot stand
      // where it does.
      setCurrentTime((time) => Math.min(time, videoDuration));
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
