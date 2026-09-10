// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { ConfirmDeleteDialog, makeDeleteConfirmation, type DeleteConfirmation } from './ConfirmDeleteDialog';

const base: DeleteConfirmation = {
  title: 'Delete "Ranked win"?',
  names: ['Ranked win'],
  confirmLabel: 'Move to trash',
  retentionHours: 24,
};

function renderDialog(
  confirmation: Partial<DeleteConfirmation> = {},
  handlers: { onCancel?: () => void; onConfirm?: (permanent: boolean, checked: boolean) => void } = {},
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

describe('makeDeleteConfirmation', () => {
  it('names a single item with quotes and offers the trash path by default', () => {
    const confirmation = makeDeleteConfirmation({ names: ['Ranked win'], retentionHours: 24 });
    expect(confirmation).toMatchObject({
      title: 'Delete "Ranked win"?',
      confirmLabel: 'Move to trash',
      affectedCount: 1,
    });
    expect(confirmation.permanentOnly).toBeUndefined();
    expect(confirmation.checkbox).toBeUndefined();
  });

  it('pluralises the heading and the button label for a bulk delete', () => {
    const confirmation = makeDeleteConfirmation({ names: ['a', 'b', 'c'], retentionHours: 24 });
    expect(confirmation.title).toBe('Delete 3 items?');
    expect(confirmation.confirmLabel).toBe('Move 3 to trash');
    expect(confirmation.affectedCount).toBe(3);
  });

  it('quotes what actually moves when that differs from the names', () => {
    const confirmation = makeDeleteConfirmation({
      names: ['a', 'b'],
      retentionHours: 24,
      affectedCount: 1,
    });
    expect(confirmation.affectedCount).toBe(1);
  });

  it('offers the cascade checkbox only when asked, and defaults it from the caller', () => {
    const plain = makeDeleteConfirmation({ names: ['a'], retentionHours: 24 });
    expect(plain.checkbox).toBeUndefined();
    expect(plain.cascadeCount).toBeUndefined();

    const cascaded = makeDeleteConfirmation({
      names: ['a'],
      retentionHours: 24,
      hasCascade: true,
      cascadeCount: 4,
      deleteLinkedHighlightsDefault: true,
    });
    expect(cascaded.cascadeCount).toBe(4);
    expect(cascaded.checkbox).toEqual({
      label: 'Delete linked highlights (favourited highlights are kept)',
      defaultChecked: true,
    });
  });

  it('frames an already-deleted item as permanent, with no trash path left', () => {
    const confirmation = makeDeleteConfirmation({
      names: ['a', 'b'],
      retentionHours: 24,
      permanentOnly: true,
    });
    expect(confirmation).toMatchObject({
      title: 'Delete 2 items for good?',
      confirmLabel: 'Delete permanently',
      permanentOnly: true,
    });
    expect(confirmation.checkbox).toBeUndefined();
  });

  it('lets a caller override the heading and label, for the empty-the-trash case', () => {
    const confirmation = makeDeleteConfirmation({
      names: ['a', 'b'],
      retentionHours: 24,
      permanentOnly: true,
      title: 'Empty the trash?',
      confirmLabel: 'Empty trash',
    });
    expect(confirmation.title).toBe('Empty the trash?');
    expect(confirmation.confirmLabel).toBe('Empty trash');
    expect(confirmation.permanentOnly).toBe(true);
  });
});

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
    expect(onConfirm).toHaveBeenCalledWith(false, false);
  });

  it('rewrites the notice and the button when skip-trash is ticked, and confirms permanently', () => {
    const onConfirm = vi.fn();
    renderDialog({}, { onConfirm });

    fireEvent.click(screen.getByRole('checkbox', { name: /skip trash/i }));

    expect(screen.getByTestId('confirm-delete-notice').textContent).toBe(
      '1 item will be deleted from disk immediately. This cannot be undone.',
    );
    fireEvent.click(screen.getByRole('button', { name: 'Delete permanently' }));
    expect(onConfirm).toHaveBeenCalledWith(true, false);
  });

  it('offers no checkbox for something already in the trash, and always confirms permanently', () => {
    const onConfirm = vi.fn();
    renderDialog({ permanentOnly: true, confirmLabel: 'Delete permanently' }, { onConfirm });

    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('cannot be undone');
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(onConfirm).toHaveBeenCalledWith(true, false);
  });
});

describe('ConfirmDeleteDialog optional checkbox', () => {
  const checkbox = { label: 'Delete linked highlights (favourited highlights are kept)' };

  it('defaults off and returns a user-selected value independently of skip-trash', () => {
    const onConfirm = vi.fn();
    renderDialog({ checkbox }, { onConfirm });
    const linked = screen.getByRole('checkbox', { name: checkbox.label });
    expect((linked as HTMLInputElement).checked).toBe(false);

    fireEvent.click(linked);
    fireEvent.click(screen.getByRole('checkbox', { name: /skip trash/i }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(onConfirm).toHaveBeenCalledWith(true, true);
  });

  it('uses the specified default for each newly mounted dialog', () => {
    const onConfirm = vi.fn();
    renderDialog({ checkbox: { ...checkbox, defaultChecked: true } }, { onConfirm });

    expect((screen.getByRole('checkbox', { name: checkbox.label }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(onConfirm).toHaveBeenCalledWith(false, true);
  });
});

describe('ConfirmDeleteDialog affected count', () => {
  const checkbox = { label: 'Delete linked highlights (favourited highlights are kept)' };

  it('counts only physically-deleted targets', () => {
    renderDialog({ affectedCount: 0, checkbox });
    expect(screen.getByTestId('confirm-delete-notice').textContent).toBe(
      '0 items will be moved to the trash, where they can be restored for the next 1 day.',
    );
  });

  it('adds the cascaded highlights once the checkbox is ticked', () => {
    const onConfirm = vi.fn();
    renderDialog({ affectedCount: 1, cascadeCount: 3, checkbox }, { onConfirm });

    const linked = screen.getByRole('checkbox', { name: checkbox.label });
    fireEvent.click(linked);

    expect(screen.getByTestId('confirm-delete-notice').textContent).toBe(
      '4 items will be moved to the trash, where they can be restored for the next 1 day.',
    );
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(onConfirm).toHaveBeenCalledWith(false, true);
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
