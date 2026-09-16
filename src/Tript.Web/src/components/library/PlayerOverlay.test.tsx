// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PlayerOverlay } from './PlayerOverlay';

afterEach(cleanup);

describe('PlayerOverlay', () => {
  it('names itself after the item it is playing', () => {
    render(
      <PlayerOverlay title="Ranked win" onClose={() => {}}>
        <p>player</p>
      </PlayerOverlay>,
    );
    expect(screen.getByRole('dialog', { name: 'Player: Ranked win' })).toBeTruthy();
    expect(screen.getByTestId('player-overlay-title').textContent).toBe('Ranked win');
  });

  it('moves focus in on mount and restores it to the opener on unmount', () => {
    const trigger = document.createElement('button');
    document.body.append(trigger);
    trigger.focus();

    const view = render(
      <PlayerOverlay title="Ranked win" onClose={() => {}}>
        <button type="button">Create clip</button>
      </PlayerOverlay>,
    );
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Close player' }));

    view.unmount();
    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });

  it('closes on Escape and on the close affordance', () => {
    const onClose = vi.fn();
    render(
      <PlayerOverlay title="Ranked win" onClose={onClose}>
        <p>player</p>
      </PlayerOverlay>,
    );
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Close player' }));
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('traps Tab inside the overlay, in both directions', () => {
    render(
      <PlayerOverlay title="Ranked win" onClose={() => {}}>
        <button type="button">Create clip</button>
      </PlayerOverlay>,
    );
    const close = screen.getByRole('button', { name: 'Close player' });
    const last = screen.getByRole('button', { name: 'Create clip' });

    last.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(close);

    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(last);
  });

  it('pulls focus back when it has escaped the overlay entirely', () => {
    const outside = document.createElement('button');
    document.body.append(outside);
    render(
      <PlayerOverlay title="Ranked win" onClose={() => {}}>
        <button type="button">Create clip</button>
      </PlayerOverlay>,
    );
    outside.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Close player' }));
    outside.remove();
  });

  it('leaves Escape and Tab to a nested modal while one is open', () => {
    const onClose = vi.fn();
    render(
      <PlayerOverlay title="Ranked win" onClose={onClose}>
        {}
        <div role="dialog" aria-modal="true" aria-label="Create clip">
          <button type="button">Create</button>
        </div>
      </PlayerOverlay>,
    );

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();

    const inner = screen.getByRole('button', { name: 'Create' });
    inner.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(inner);
  });
});
