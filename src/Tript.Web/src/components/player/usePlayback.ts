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
//
// The fallback is a *guess* and the hook says so: `durationKnown` is false until the media itself
// reports its length. That distinction is load-bearing rather than cosmetic — the caller's fallback
// can be a fabricated constant (DEFAULT_SESSION_SECONDS = 120s for a recording with no metadata
// record), and clip segments clamped against it were allowed to run past the end of an 8s file. Only
// a duration the media vouched for may be used as a bound; see clipModel's `resolveClipBounds`.

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
  const duration = mediaDuration ?? Math.max(0, fallbackDuration);
  const durationKnown = mediaDuration !== null;

  // A change of session resets the playhead — keyed on the item identity, not the duration, so
  // navigating between two sessions of the same length still resets, and a metadata update for the
  // session being watched does not yank the playhead back to 0.
  useEffect(() => {
    setCurrentTime(0);
    setMediaDuration(null);
    setPlaying(false);
    const video = videoRef.current;
    if (video) {
      video.currentTime = 0;
      video.pause();
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
