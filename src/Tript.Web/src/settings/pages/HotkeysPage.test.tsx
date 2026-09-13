// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { HotkeysPage } from './HotkeysPage';
import type { HotkeySettings } from '../settingsModel';

const HOTKEYS: HotkeySettings = {
  enabled: true,
  toggleRecording: { modifiers: ['Control', 'Alt'], key: 'KeyR' },
  manualBookmark: { modifiers: ['Control', 'Alt'], key: 'KeyB' },
  quickClip: { modifiers: ['Control', 'Alt'], key: 'KeyC' },
  quickClipSeconds: 30,
};

function renderPage(settings: HotkeySettings = HOTKEYS) {
  const update = vi.fn();
  render(<HotkeysPage settings={settings} update={update} page="hotkeys" />);
  return update;
}

afterEach(cleanup);

describe('HotkeysPage', () => {
  it('renders the stored bindings', () => {
    renderPage();

    expect(screen.getByLabelText('Toggle recording hotkey').textContent).toBe('Control + Alt + R');
    expect(screen.getByLabelText('Manual bookmark hotkey').textContent).toBe('Control + Alt + B');
    expect(screen.getByLabelText('Quick clip hotkey').textContent).toBe('Control + Alt + C');
  });

  it('shows Unbound for a cleared binding', () => {
    renderPage({ ...HOTKEYS, quickClip: { modifiers: [], key: null } });
    expect(screen.getByLabelText('Quick clip hotkey').textContent).toBe('Unbound');
  });

  it('sends a hotkeys page patch when the master toggle changes', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Enable global hotkeys'));

    expect(update).toHaveBeenCalledWith('hotkeys', { enabled: false });
  });

  it('sends a hotkeys page patch with the chosen quick clip length', () => {
    const update = renderPage();
    fireEvent.change(screen.getByLabelText('Quick clip length in seconds'), { target: { value: '15' } });

    expect(update).toHaveBeenCalledWith('hotkeys', { quickClipSeconds: 15 });
  });

  it('captures a new binding from the next keydown after clicking', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Manual bookmark hotkey'));
    expect(screen.getByLabelText('Manual bookmark hotkey').textContent).toBe('Press a key…');

    fireEvent.keyDown(window, { code: 'KeyK', ctrlKey: true, shiftKey: true });

    expect(update).toHaveBeenCalledWith('hotkeys', {
      manualBookmark: { modifiers: ['Control', 'Shift'], key: 'KeyK' },
    });
  });

  it('cancels capture on Escape without changing the binding', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Quick clip hotkey'));

    fireEvent.keyDown(window, { code: 'Escape' });

    expect(update).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Quick clip hotkey').textContent).toBe('Control + Alt + C');
  });

  it('clears the binding on Backspace', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Quick clip hotkey'));

    fireEvent.keyDown(window, { code: 'Backspace' });

    expect(update).toHaveBeenCalledWith('hotkeys', { quickClip: { modifiers: [], key: null } });
  });

  it('ignores a bare modifier keydown and keeps waiting for the real key', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Toggle recording hotkey'));

    fireEvent.keyDown(window, { code: 'ControlLeft', ctrlKey: true });
    expect(screen.getByLabelText('Toggle recording hotkey').textContent).toBe('Press a key…');

    fireEvent.keyDown(window, { code: 'KeyX', ctrlKey: true });
    expect(update).toHaveBeenCalledWith('hotkeys', {
      toggleRecording: { modifiers: ['Control'], key: 'KeyX' },
    });
  });
});
