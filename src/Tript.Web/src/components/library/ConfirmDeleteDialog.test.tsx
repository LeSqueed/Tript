// SPDX-License-Identifier: GPL-2.0-or-later
//
// The delete confirmation's modal behaviour. Everything here is a safety property rather than a
// look: focus cannot leave the dialog, Escape cancels rather than confirms, the destructive button
// is never what focus lands on, and the sentence the user reads before pressing it says the truth
// about where the items go — including when the host is configured never to auto-purge.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { ConfirmDeleteDialog, type DeleteConfirmation } from './ConfirmDeleteDialog';

const base: DeleteConfirmation = {
  title: 'Delete "Ranked win"?',
  names: ['Ranked win'],
  confirmLabel: 'Move to trash',
  retentionHours: 24,
};

function renderDialog(
  confirmation: Partial<DeleteConfirmation> = {},
  handlers: { onCancel?: () => void; onConfirm?: (permanent: boolean) => void } = {},
) {
  return render(
    <ConfirmDeleteDialog
      confirmation={{ ...base, ...confirmation }}
      onCancel={handlers.onCancel ?? (() => {})}
      onConfirm={handlers.onConfirm ?? (() => {})}
    />,
  );
}

afterEach(cleanup);

describe('ConfirmDeleteDialog content', () => {
  it('names what it is about to delete and says where it goes', () => {
    renderDialog();
    expect(screen.getByRole('dialog', { name: 'Delete "Ranked win"?' })).toBeTruthy();
    expect(screen.getByTestId('confirm-delete-notice').textContent).toBe(
      '1 item will be moved to the trash, where it can be restored for the next 1 day.',
    );
    expect(within(screen.getByTestId('confirm-delete-items')).getByText('Ranked win')).toBeTruthy();
  });

  it('lists a small bulk delete in full', () => {
    renderDialog({ title: 'Delete 3 items?', names: ['a', 'b', 'c'] });
    const items = within(screen.getByTestId('confirm-delete-items')).getAllByRole('listitem');
    expect(items.map((li) => li.textContent)).toEqual(['a', 'b', 'c']);
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('3 items');
  });

  it('summarises instead of listing once naming them stops helping', () => {
    renderDialog({ title: 'Delete 8 items?', names: ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'] });
    expect(screen.queryByTestId('confirm-delete-items')).toBeNull();
    expect(screen.getByTestId('confirm-delete-summary').textContent).toBe('a, b, c and 5 more');
    // The count is still exact, even though the names are not all there.
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('8 items');
  });

  it('quotes the retention it was given rather than a hardcoded 24 hours', () => {
    renderDialog({ retentionHours: 72 });
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain(
      'for the next 3 days',
    );
  });

  it('promises no window at all when the host never auto-purges', () => {
    renderDialog({ retentionHours: 0 });
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain(
      'until you empty it',
    );
  });
});

describe('ConfirmDeleteDialog skip-trash', () => {
  it('confirms to the trash by default', () => {
    const onConfirm = vi.fn();
    renderDialog({}, { onConfirm });
    expect((screen.getByRole('checkbox') as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByRole('button', { name: 'Move to trash' }));
    expect(onConfirm).toHaveBeenCalledWith(false);
  });

  it('rewrites the notice and the button when skip-trash is ticked, and confirms permanently', () => {
    const onConfirm = vi.fn();
    renderDialog({}, { onConfirm });

    fireEvent.click(screen.getByRole('checkbox', { name: /skip trash/i }));

    expect(screen.getByTestId('confirm-delete-notice').textContent).toBe(
      '1 item will be deleted from disk immediately. This cannot be undone.',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete permanently' }));
    expect(onConfirm).toHaveBeenCalledWith(true);
  });

  it('offers no checkbox for something already in the trash, and always confirms permanently', () => {
    const onConfirm = vi.fn();
    renderDialog({ permanentOnly: true, confirmLabel: 'Delete permanently' }, { onConfirm });

    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('cannot be undone');
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(onConfirm).toHaveBeenCalledWith(true);
  });
});

describe('ConfirmDeleteDialog keyboard', () => {
  it('focuses Cancel on open, not the destructive button', () => {
    renderDialog();
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Cancel' }));
  });

  it('restores focus to whatever opened it', () => {
    const trigger = document.createElement('button');
    document.body.append(trigger);
    trigger.focus();

    const view = renderDialog();
    expect(document.activeElement).not.toBe(trigger);

    view.unmount();
    expect(document.activeElement).toBe(trigger);
    trigger.remove();
  });

  it('cancels on Escape and on Cancel, and never confirms by keyboard alone', () => {
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    renderDialog({}, { onCancel, onConfirm });

    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onCancel).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCancel).toHaveBeenCalledTimes(2);
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('traps Tab inside the dialog, in both directions', () => {
    renderDialog();
    const checkbox = screen.getByRole('checkbox');
    const confirm = screen.getByRole('button', { name: 'Move to trash' });

    confirm.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(checkbox);

    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(confirm);
  });

  it('pulls focus back in when it has escaped the dialog entirely', () => {
    const outside = document.createElement('button');
    document.body.append(outside);
    renderDialog();

    outside.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(screen.getByRole('checkbox'));
    outside.remove();
  });
});
