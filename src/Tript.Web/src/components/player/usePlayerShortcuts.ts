// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef } from 'react';

export interface PlayerShortcutHandlers {
  onEscape: () => void;
  onDelete?: () => void;
  onFavorite?: () => void;
  onMarkIn: () => void;
  onMarkOut: () => void;
  onQuickClip: () => void;
  onBookmark: () => void;
  onTogglePlay: () => void;
  onNavigate: (delta: number) => void;
  onSeekBy: (seconds: number) => void;
}

const INTERACTIVE = 'button, a[href], input, textarea, select, [contenteditable="true"], [role="slider"], [role="radio"]';
const SEEK_STEP_SECONDS = 5;

export function usePlayerShortcuts(handlers: PlayerShortcutHandlers): void {
  const latest = useRef(handlers);
  latest.current = handlers;

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      const current = latest.current;
      const eventTarget = event.target instanceof HTMLElement ? event.target : null;
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const target = eventTarget && eventTarget !== document.body ? eventTarget : activeElement;
      if (document.querySelector('[role="dialog"]') || target?.closest(INTERACTIVE)) {
        return;
      }
      if (event.ctrlKey || event.metaKey || event.altKey) {
        return;
      }
      const key = event.key.toLowerCase();
      if (event.repeat && event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        current.onEscape();
        return;
      }
      if (key === 'delete' && current.onDelete) {
        event.preventDefault();
        current.onDelete();
        return;
      }
      if (key === 'f' && current.onFavorite) {
        event.preventDefault();
        current.onFavorite();
        return;
      }
      const letterAction = ({
        i: current.onMarkIn,
        o: current.onMarkOut,
        m: current.onQuickClip,
        b: current.onBookmark,
      } as Record<string, (() => void) | undefined>)[key];
      if (letterAction) {
        event.preventDefault();
        letterAction();
        return;
      }
      if (event.code === 'Space') {
        event.preventDefault();
        current.onTogglePlay();
      } else if (event.shiftKey && event.key === 'ArrowLeft') {
        event.preventDefault();
        current.onNavigate(-1);
      } else if (event.shiftKey && event.key === 'ArrowRight') {
        event.preventDefault();
        current.onNavigate(1);
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        current.onSeekBy(-SEEK_STEP_SECONDS);
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        current.onSeekBy(SEEK_STEP_SECONDS);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);
}
