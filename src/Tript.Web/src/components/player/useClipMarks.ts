// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useState } from 'react';
import { buildDefaultRegion, clampTime, MIN_REGION_SECONDS, newRegionId } from './clipModel';

interface RegionMarker {
  markRegion: (start: number, end: number) => void;
}

export interface ClipMarks {
  markInTime: number | null;
  clearMarkIn: () => void;
  markIn: () => void;
  markOut: () => void;
  markSegmentAtPlayhead: () => void;
}

export function useClipMarks({
  dialog,
  currentTime,
  clipDuration,
  canMark,
  filePath,
}: {
  dialog: RegionMarker;
  currentTime: number;
  clipDuration: number;
  canMark: boolean;
  filePath: string | undefined;
}): ClipMarks {
  const [markInTime, setMarkInTime] = useState<number | null>(null);

  useEffect(() => {
    setMarkInTime(null);
  }, [filePath]);

  const clearMarkIn = useCallback(() => setMarkInTime(null), []);

  const markIn = useCallback(() => {
    if (!canMark) {
      return;
    }
    setMarkInTime(clampTime(currentTime, clipDuration));
  }, [canMark, currentTime, clipDuration]);

  const markOut = useCallback(() => {
    if (markInTime === null) {
      return;
    }
    const out = clampTime(currentTime, clipDuration);
    if (out <= markInTime) {
      return;
    }
    dialog.markRegion(markInTime, out);
    if (Math.abs(out - markInTime) >= MIN_REGION_SECONDS) {
      setMarkInTime(null);
    }
  }, [markInTime, currentTime, clipDuration, dialog]);

  const markSegmentAtPlayhead = useCallback(() => {
    const region = buildDefaultRegion(currentTime, clipDuration, newRegionId());
    dialog.markRegion(region.start, region.end);
    setMarkInTime(null);
  }, [currentTime, clipDuration, dialog]);

  return { markInTime, clearMarkIn, markIn, markOut, markSegmentAtPlayhead };
}
