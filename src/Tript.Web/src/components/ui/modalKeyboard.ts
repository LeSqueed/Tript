// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, type RefObject } from 'react';

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

const PREVIOUS_KEYS = new Set(['ArrowLeft', 'ArrowUp']);
const NEXT_KEYS = new Set(['ArrowRight', 'ArrowDown']);

function isEditable(element: Element | null): boolean {
  if (!(element instanceof HTMLElement)) return false;
  if (element.isContentEditable) return true;
  return element instanceof HTMLInputElement
    || element instanceof HTMLTextAreaElement
    || element instanceof HTMLSelectElement;
}

export function moveFocusWithArrows(container: HTMLElement, event: KeyboardEvent): boolean {
  const previous = PREVIOUS_KEYS.has(event.key);
  if (!previous && !NEXT_KEYS.has(event.key)) return false;
  if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return false;
  const active = document.activeElement;
  if (isEditable(active)) return false;

  const buttons = [...container.querySelectorAll<HTMLButtonElement>('button:not([disabled])')];
  if (buttons.length === 0) return false;
  const index = active instanceof HTMLButtonElement ? buttons.indexOf(active) : -1;
  const target = index < 0
    ? buttons[previous ? buttons.length - 1 : 0]
    : buttons[(index + (previous ? -1 : 1) + buttons.length) % buttons.length];
  event.preventDefault();
  target.focus();
  return true;
}

export function useModalKeyboard(
  containerRef: RefObject<HTMLElement | null>,
  initialFocusRef: RefObject<HTMLElement | null>,
  onCancel: () => void,
): void {
  const onCancelRef = useRef(onCancel);
  onCancelRef.current = onCancel;

  useEffect(() => {
    const previouslyFocused = document.activeElement;
    initialFocusRef.current?.focus();
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
      if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        onCancelRef.current();
        return;
      }
      if (moveFocusWithArrows(container, event)) {
        return;
      }
      if (event.key !== 'Tab') {
        return;
      }
      const focusable = [...container.querySelectorAll<HTMLElement>(FOCUSABLE)];
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
}
