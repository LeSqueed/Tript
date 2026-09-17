// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useRef } from 'react';
import type { TimelineRegion } from './clipSeam';
import { computeEditSeek, computeLoopDecision } from './clipLoop';
import { clampTime } from './clipModel';

export function useRegionLooping({
  currentTime,
  playing,
  regions,
  selectedRegionId,
  duration,
  clipDuration,
  seek,
}: {
  currentTime: number;
  playing: boolean;
  regions: TimelineRegion[];
  selectedRegionId: string | null;
  duration: number;
  clipDuration: number;
  seek: (time: number) => void;
}): void {
  const lastSampleRef = useRef<number | null>(null);
  useEffect(() => {
    const previousTime = lastSampleRef.current;
    lastSampleRef.current = currentTime;
    if (previousTime === null) {
      return;
    }
    const decision = computeLoopDecision(currentTime, previousTime, playing, regions, selectedRegionId);
    if (decision.shouldLoopBack && decision.region) {
      seek(clampTime(decision.region.start, duration));
    }
  }, [currentTime, playing, regions, selectedRegionId, seek, duration]);

  const selectedRegion = useMemo(
    () => regions.find((region) => region.id === selectedRegionId) ?? null,
    [regions, selectedRegionId],
  );
  const loopBoundsRef = useRef<TimelineRegion | null>(null);
  const loopDurationRef = useRef(clipDuration);
  const reconcilingRef = useRef(false);
  useEffect(() => {
    const previousBounds = loopBoundsRef.current;
    loopBoundsRef.current = selectedRegion;
    const spendingGrace = reconcilingRef.current;
    if (loopDurationRef.current !== clipDuration) {
      loopDurationRef.current = clipDuration;
      reconcilingRef.current = true;
      return;
    }
    reconcilingRef.current = false;
    if (spendingGrace) {
      return;
    }
    const target = computeEditSeek(previousBounds, selectedRegion, currentTime);
    if (target !== null) {
      seek(clampTime(target, duration));
    }
  }, [selectedRegion, currentTime, clipDuration, duration, seek]);
}
