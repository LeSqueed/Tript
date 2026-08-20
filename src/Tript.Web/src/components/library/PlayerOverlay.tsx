// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player overlay — the player as a full-bleed layer over the library, not a route of its own.
// Navigating away and back would unmount the library and lose the user's place; layering over it
// keeps it mounted underneath.

import { useEffect, useRef, type ReactNode } from 'react';
import { Button } from '../../components/ui/controls';

/**
 * What can hold focus inside the overlay. The player's video is focusable through its `tabindex`:
 * it carries no `controls` attribute, because those would paint browser chrome over the picture
 * (see TransportBar), so the tabindex clause is what covers it.
 */
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
  /** The item's display name — the overlay's accessible name, so the layer says what it is showing. */
  title: string;
  onClose: () => void;
  children: ReactNode;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  // `onClose` is read from a ref by the key handler so the listener is installed once, on mount, and
  // is not torn down and rebuilt every time the parent re-renders with a new callback identity.
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  // Move focus in on mount, and put it back on unmount — whichever way the overlay was closed
  // (Escape, the close button, or the item disappearing from under it).
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
      // A nested modal owns the keyboard while it is open. `querySelector` on the container cannot
      // match the container itself, so this only ever finds a modal *inside* us (the clip dialog).
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
      // Focus that has escaped the overlay (or never entered it) is pulled back to the first stop
      // rather than left where it is — the trap has to be closed in both directions.
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
        {/* A close, not a back: this is a dialog over the library, and the library's own nav item
          * is still on screen behind it. Giving both the same chevron-and-"Library" treatment would
          * put two identically named buttons in front of the user at compact width. */}
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
