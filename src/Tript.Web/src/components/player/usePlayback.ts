// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import type { RefObject } from 'react';
import { reportClientError } from '../../app/errorReporting';

// NotAllowedError (autoplay policy) and AbortError (a pause or a new source interrupting play) are
// routine. Anything else, NotSupportedError above all, is a clip that will not play, which used to
// fail with no trace, and is the first thing to look for when an HDR or codec change breaks playback.
function reportPlaybackFailure(error: unknown): void {
  if (error instanceof DOMException && (error.name === 'NotAllowedError' || error.name === 'AbortError')) return;
  reportClientError('playback', error);
}

export interface PlaybackState {
  currentTime: number;
  duration: number;
  durationKnown: boolean;
  playing: boolean;
  seek(time: number): void;
  togglePlayPause(): void;
  prepareItemChange(shouldPlay: boolean): void;
  onVideoTimeUpdate(time: number): void;
  onVideoDuration(duration: number): void;
  onVideoPlay(): void;
  onVideoPause(): void;
  onVideoEnded(): void;
  videoRef: RefObject<HTMLVideoElement | null>;
}

export function usePlayback(itemKey: string, fallbackDuration: number, visible = true): PlaybackState {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const [currentTime, setCurrentTime] = useState(0);
  const [mediaDuration, setMediaDuration] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  const nextItemShouldPlay = useRef<boolean | null>(null);
  const duration = mediaDuration ?? Math.max(0, fallbackDuration);
  const durationKnown = mediaDuration !== null;

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
        void video.play().catch(reportPlaybackFailure);
      }
    }
  }, [itemKey]);

  useEffect(() => {
    if (!visible) {
      videoRef.current?.pause();
    }
  }, [visible]);

  useEffect(() => {
    if (!playing || typeof requestAnimationFrame !== 'function') {
      return;
    }
    let frame = 0;
    const sample = (): void => {
      const video = videoRef.current;
      if (video) {
        setCurrentTime(video.currentTime);
      }
      frame = requestAnimationFrame(sample);
    };
    frame = requestAnimationFrame(sample);
    return () => cancelAnimationFrame(frame);
  }, [playing]);

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
      void video.play().catch(reportPlaybackFailure);
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
      if (!Number.isFinite(videoDuration) || videoDuration <= 0) {
        return;
      }
      setMediaDuration(videoDuration);
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
