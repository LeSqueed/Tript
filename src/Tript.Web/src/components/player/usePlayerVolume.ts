// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

export interface PlayerVolume {
  volume: number;
  muted: boolean;
  playbackRate: number;
  changeVolume: (next: number) => void;
  toggleMute: () => void;
  setPlaybackRate: (next: number) => void;
}

export function usePlayerVolume(videoRef: RefObject<HTMLVideoElement | null>, mediaKey: unknown): PlayerVolume {
  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);
  const [playbackRate, setPlaybackRate] = useState(1);
  const lastAudibleVolume = useRef(1);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }
    video.volume = volume;
    video.muted = muted;
    video.playbackRate = playbackRate;
    video.defaultPlaybackRate = playbackRate;
  }, [videoRef, volume, muted, playbackRate, mediaKey]);

  const changeVolume = useCallback((next: number) => {
    setVolume(next);
    setMuted(next === 0);
    if (next > 0) {
      lastAudibleVolume.current = next;
    }
  }, []);

  const toggleMute = useCallback(() => {
    if (!muted) {
      setMuted(true);
      return;
    }
    if (volume === 0) {
      setVolume(lastAudibleVolume.current || 1);
    }
    setMuted(false);
  }, [muted, volume]);

  return { volume, muted, playbackRate, changeVolume, toggleMute, setPlaybackRate };
}
