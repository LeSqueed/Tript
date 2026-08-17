// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player overlay — the player as a full-bleed layer over the library, not a route of its own.
//
// WHY IT IS AN OVERLAY AND NOT A PAGE. The library is the single home: a user reviewing a session came
// from a particular filter, sort and page, and going "back" has to return them to exactly that, scroll
// position included. A route swap cannot promise this, because leaving the library unmounts it and
// takes its state with it. An overlay keeps the library mounted underneath and merely covers it, so
// there is no state to save or restore — closing the overlay reveals the library the user left. (The
// shell handles the one thing the DOM does not keep by itself: the scroll offset of its content
// column, which it hides while the overlay is up so the page behind cannot be scrolled away.)
//
// This component is chrome only. It renders `children` — the existing `PlayerView`, unchanged and
// unforked — plus the two things a layer over a page owes the user: a visible way out, and a keyboard
// that behaves like a modal's.
//
// FOCUS. On mount the overlay remembers what was focused (the card that opened it, by construction)
// and moves focus inside; on unmount it puts focus back. Tab is trapped, so a player over a library
// cannot leak focus into the cards it is covering — which is what makes the overlay honest about
// being modal rather than merely looking modal.
//
// ESCAPE, AND WHY IT SOMETIMES DOES NOTHING HERE. The player can open the clip dialog, which is itself
// a modal (`role="dialog" aria-modal="true"`). While something like that is open inside us, Escape
// belongs to it, not to us: closing the whole player out from under an open dialog would discard the
// user's marked segments. So the handler stands down when it finds a nested modal, rather than racing
// it. The listener is on the document in the CAPTURE phase so it sees the key before the player's own
// window-level shortcut handler does.

import { useEffect, useRef, type ReactNode } from 'react';

/** What can hold focus inside the overlay. `video[controls]` matters — the player's video is one. */
const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), video[controls], [tabindex]:not([tabindex="-1"])';

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
        <button
          type="button"
          ref={closeRef}
          className="btn ghost"
          onClick={onClose}
          aria-label="Close player"
        >
          ← Back to library
        </button>
        <span className="player-overlay-title" data-testid="player-overlay-title">
          {title}
        </span>
        <span className="player-overlay-hint muted small">Esc closes the player</span>
      </div>
      <div className="player-overlay-body">{children}</div>
    </div>
  );
}
