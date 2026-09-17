// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, renderHook } from '@testing-library/react';
import { usePlayerShortcuts, type PlayerShortcutHandlers } from './usePlayerShortcuts';

function handlers(overrides: Partial<PlayerShortcutHandlers> = {}): PlayerShortcutHandlers {
  return {
    onEscape: vi.fn(),
    onMarkIn: vi.fn(),
    onMarkOut: vi.fn(),
    onQuickClip: vi.fn(),
    onBookmark: vi.fn(),
    onTogglePlay: vi.fn(),
    onNavigate: vi.fn(),
    onSeekBy: vi.fn(),
    ...overrides,
  };
}

function press(key: string, init: KeyboardEventInit = {}, target: EventTarget = window) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(event);
  return event;
}

describe('usePlayerShortcuts', () => {
  afterEach(() => {
    cleanup();
    document.body.innerHTML = '';
  });

  it('maps the player keys to their actions', () => {
    const actions = handlers();
    renderHook(() => usePlayerShortcuts(actions));

    press('i');
    press('O');
    press('m');
    press('b');
    press(' ', { code: 'Space' });
    press('ArrowLeft');
    press('ArrowRight', { shiftKey: true });
    const escape = press('Escape');

    expect(actions.onMarkIn).toHaveBeenCalledOnce();
    expect(actions.onMarkOut).toHaveBeenCalledOnce();
    expect(actions.onQuickClip).toHaveBeenCalledOnce();
    expect(actions.onBookmark).toHaveBeenCalledOnce();
    expect(actions.onTogglePlay).toHaveBeenCalledOnce();
    expect(actions.onSeekBy).toHaveBeenCalledWith(-5);
    expect(actions.onNavigate).toHaveBeenCalledWith(1);
    expect(actions.onEscape).toHaveBeenCalledOnce();
    expect(escape.defaultPrevented).toBe(true);
  });

  it('leaves delete and favourite alone when the player has no handler for them', () => {
    const actions = handlers();
    renderHook(() => usePlayerShortcuts(actions));

    expect(press('Delete').defaultPrevented).toBe(false);
    expect(press('f').defaultPrevented).toBe(false);
  });

  it('ignores keys typed into controls, while a dialog is open, or with modifiers', () => {
    const actions = handlers();
    renderHook(() => usePlayerShortcuts(actions));
    const input = document.createElement('input');
    document.body.append(input);

    press('b', {}, input);
    press('b', { ctrlKey: true });
    press('b', { repeat: true });
    const dialog = document.createElement('div');
    dialog.setAttribute('role', 'dialog');
    document.body.append(dialog);
    press('b');

    expect(actions.onBookmark).not.toHaveBeenCalled();
  });

  it('always calls the latest handlers', () => {
    const first = handlers();
    const second = handlers();
    const { rerender } = renderHook(({ current }) => usePlayerShortcuts(current), {
      initialProps: { current: first },
    });

    rerender({ current: second });
    press('b');

    expect(first.onBookmark).not.toHaveBeenCalled();
    expect(second.onBookmark).toHaveBeenCalledOnce();
  });
});
