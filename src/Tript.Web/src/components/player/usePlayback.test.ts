// SPDX-License-Identifier: GPL-2.0-or-later
//
// The playback hook's sample path: while playing, the playhead follows the element's position every
// animation frame rather than waiting for the element's sparse `timeupdate` events.

import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { usePlayback } from './usePlayback';

function attachVideo(result: { current: ReturnType<typeof usePlayback> }): HTMLVideoElement {
  const video = document.createElement('video');
  result.current.videoRef.current = video;
  return video;
}

describe('usePlayback', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('Playing_SamplesThePlayheadEveryAnimationFrameWithoutTimeupdate', () => {
    const { result } = renderHook(() => usePlayback('a.mp4', 120));
    const video = attachVideo(result);

    act(() => {
      result.current.onVideoPlay();
    });
    video.currentTime = 12.5;
    act(() => {
      vi.advanceTimersByTime(50);
    });

    expect(result.current.currentTime).toBe(12.5);

    video.currentTime = 12.75;
    act(() => {
      vi.advanceTimersByTime(50);
    });

    expect(result.current.currentTime).toBe(12.75);
  });

  it('Paused_StopsSamplingAndLeavesThePlayheadWhereItStood', () => {
    const { result } = renderHook(() => usePlayback('a.mp4', 120));
    const video = attachVideo(result);

    act(() => {
      result.current.onVideoPlay();
    });
    video.currentTime = 20;
    act(() => {
      vi.advanceTimersByTime(50);
    });
    act(() => {
      result.current.onVideoPause();
    });
    video.currentTime = 45;
    act(() => {
      vi.advanceTimersByTime(200);
    });

    expect(result.current.currentTime).toBe(20);

    // While paused the element's own timeupdate (a seek, a frame step) still moves the playhead.
    act(() => {
      result.current.onVideoTimeUpdate(45);
    });
    expect(result.current.currentTime).toBe(45);
  });

  it('Seek_WhilePlaying_IsNotOverwrittenByTheNextSample', () => {
    const { result } = renderHook(() => usePlayback('a.mp4', 120));
    const video = attachVideo(result);

    act(() => {
      result.current.onVideoPlay();
    });
    video.currentTime = 5;
    act(() => {
      vi.advanceTimersByTime(50);
    });
    act(() => {
      result.current.seek(60);
    });
    act(() => {
      vi.advanceTimersByTime(50);
    });

    // The seek wrote the element's position; the next sample reads it back rather than an old value.
    expect(video.currentTime).toBe(60);
    expect(result.current.currentTime).toBe(60);
  });
});
