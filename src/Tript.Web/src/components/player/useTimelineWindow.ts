// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import { zoomWindow, type WindowState } from './timelineModel';

export function useTimelineWindow(filePath: string | undefined, currentTime: number, duration: number): {
  viewWindow: WindowState;
  setAdjustedViewWindow: (next: WindowState) => void;
} {
  const [viewWindow, setViewWindow] = useState<WindowState>(() =>
    zoomWindow(0, duration, duration),
  );
  const viewWindowItem = useRef(filePath);
  const viewWindowAdjusted = useRef(false);

  useEffect(() => {
    if (viewWindowItem.current === filePath) {
      return;
    }
    viewWindowItem.current = filePath;
    viewWindowAdjusted.current = false;
    setViewWindow(zoomWindow(0, duration, duration));
  }, [filePath, duration]);

  useEffect(() => {
    setViewWindow((prev) => viewWindowAdjusted.current
      ? zoomWindow(currentTime, prev.seconds, duration)
      : zoomWindow(0, duration, duration));
  }, [currentTime, duration]);

  const setAdjustedViewWindow = useCallback((next: WindowState) => {
    viewWindowAdjusted.current = true;
    setViewWindow(next);
  }, []);

  return { viewWindow, setAdjustedViewWindow };
}
