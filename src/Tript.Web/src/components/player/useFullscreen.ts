// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

export function useFullscreen<T extends HTMLElement>(): {
  rootRef: RefObject<T | null>;
  isFullscreen: boolean;
  toggleFullscreen: () => void;
} {
  const rootRef = useRef<T>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);

  useEffect(() => {
    const onFullscreenChange = () => setIsFullscreen(document.fullscreenElement === rootRef.current);
    document.addEventListener('fullscreenchange', onFullscreenChange);
    return () => document.removeEventListener('fullscreenchange', onFullscreenChange);
  }, []);

  const toggleFullscreen = useCallback(() => {
    const root = rootRef.current;
    if (!root || !document.fullscreenEnabled) {
      return;
    }
    if (document.fullscreenElement === root) {
      void document.exitFullscreen();
    } else {
      void root.requestFullscreen();
    }
  }, []);

  return { rootRef, isFullscreen, toggleFullscreen };
}
