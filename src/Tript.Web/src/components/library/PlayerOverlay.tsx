// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, type ReactNode } from 'react';
import { Button } from '../../components/ui/controls';

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function focusableWithin(container: HTMLElement): HTMLElement[] {
  return [...container.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
    (element) => element.getAttribute('aria-hidden') !== 'true',
  );
}

export function PlayerOverlay({
  title,
  onClose,
  children,
}: {
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  useEffect(() => {
    const previouslyFocused = document.activeElement;
    closeRef.current?.focus();
    return () => {
      if (previouslyFocused instanceof HTMLElement && document.contains(previouslyFocused)) {
        previouslyFocused.focus();
      }
    };
  }, []);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      const container = containerRef.current;
      if (!container) {
        return;
      }
      const nestedModal = container.querySelector('[role="dialog"][aria-modal="true"]');

      if (event.key === 'Escape') {
        if (nestedModal) {
          return;
        }
        event.preventDefault();
        onCloseRef.current();
        return;
      }

      if (event.key !== 'Tab' || nestedModal) {
        return;
      }
      const focusable = focusableWithin(container);
      if (focusable.length === 0) {
        return;
      }
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;
      if (!(active instanceof HTMLElement) || !container.contains(active)) {
        event.preventDefault();
        first.focus();
        return;
      }
      if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus();
      } else if (event.shiftKey && active === first) {
        event.preventDefault();
        last.focus();
      }
    }

    document.addEventListener('keydown', onKeyDown, true);
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, []);

  return (
    <div
      className="player-overlay"
      data-testid="player-overlay"
      role="dialog"
      aria-modal="true"
      aria-label={`Player — ${title}`}
      ref={containerRef}
    >
      <div className="player-overlay-bar">
        {}
        <Button
          variant="ghost"
          size="small"
          ref={closeRef}
          icon="close"
          onClick={onClose}
          aria-label="Close player"
        />
        <span className="player-overlay-title" data-testid="player-overlay-title">
          {title}
        </span>
        <span className="player-overlay-hint muted small">Esc closes the player</span>
      </div>
      <div className="player-overlay-body">{children}</div>
    </div>
  );
}
