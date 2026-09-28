// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ConfirmDialog } from './confirmDialog';

afterEach(cleanup);

function renderDialog(onCancel = vi.fn()) {
  render(
    <ConfirmDialog
      title="Delete clip?"
      notice="This cannot be undone."
      confirmLabel="Delete"
      onConfirm={vi.fn()}
      onCancel={onCancel}
    />,
  );
  return {
    cancel: screen.getByRole('button', { name: 'Cancel' }),
    confirm: screen.getByRole('button', { name: 'Delete' }),
  };
}

describe('ConfirmDialog keyboard', () => {
  it('starts on Cancel and moves between buttons with the arrow keys, wrapping', () => {
    const { cancel, confirm } = renderDialog();
    expect(document.activeElement).toBe(cancel);

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(confirm);

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(cancel);

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowLeft' });
    expect(document.activeElement).toBe(confirm);

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowUp' });
    expect(document.activeElement).toBe(cancel);

    fireEvent.keyDown(document.activeElement!, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(confirm);
  });

  it('still cancels on Escape and traps Tab', () => {
    const onCancel = vi.fn();
    const { cancel, confirm } = renderDialog(onCancel);

    confirm.focus();
    fireEvent.keyDown(confirm, { key: 'Tab' });
    expect(document.activeElement).toBe(cancel);

    fireEvent.keyDown(cancel, { key: 'Escape' });
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('leaves arrow keys alone inside a text field', () => {
    render(
      <>
        <input aria-label="Outside" />
        <ConfirmDialog title="T" notice="N" confirmLabel="OK" onConfirm={vi.fn()} onCancel={vi.fn()} />
      </>,
    );
    const input = screen.getByLabelText('Outside');
    input.focus();

    fireEvent.keyDown(input, { key: 'ArrowRight' });

    expect(document.activeElement).toBe(input);
  });
});
