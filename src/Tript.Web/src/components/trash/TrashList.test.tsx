// SPDX-License-Identifier: GPL-2.0-or-later
//
// The trash screen, rendered. The formatting itself is tested without a DOM (trash/trashModel.test.ts);
// what needs a DOM is the wiring: that a restore goes out unconfirmed (it undoes something), that
// every permanent removal goes through the confirmation first, that "Empty trash" sends no entry ids
// at all, and that a selection cannot outlive the entries it names.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { TrashList } from './TrashList';
import type { TrashEntry } from '../../ipc/protocol';
import type { TrashController } from './useTrash';

/** A fixed "now": 2026-08-17T00:00:00Z in epoch seconds. */
const NOW = 1787011200;
const HOUR = 3600;

function entry(overrides: Partial<TrashEntry> & { id: string }): TrashEntry {
  return {
    contentType: 'recording',
    fileName: `${overrides.id}.mp4`,
    deletedAt: NOW - 2 * HOUR,
    purgeAt: NOW + 22 * HOUR,
    ...overrides,
  };
}

const ranked = entry({ id: 'e1', title: 'Ranked win', game: 'Counter-Strike 2', fileSizeBytes: 1_500_000_000 });
const shot = entry({ id: 'e2', contentType: 'clip', title: 'Nice shot', durationSeconds: 3 });

function controller(entries: TrashEntry[], retentionHours = 24) {
  const trash: TrashController = {
    entries,
    retentionHours,
    loaded: true,
    restore: vi.fn(),
    purge: vi.fn(),
    emptyTrash: vi.fn(),
  };
  return trash;
}

function renderTrash(entries: TrashEntry[], retentionHours = 24) {
  const trash = controller(entries, retentionHours);
  return { trash, ...render(<TrashList trash={trash} nowSeconds={NOW} />) };
}

afterEach(cleanup);

describe('TrashList listing', () => {
  it('says what each entry was, when it went, and when it stops being recoverable', () => {
    renderTrash([ranked, shot]);
    const rows = screen.getAllByTestId('trash-row');
    expect(rows).toHaveLength(2);

    const first = within(rows[0]);
    expect(first.getByText('Ranked win')).toBeTruthy();
    expect(first.getByText('Recording')).toBeTruthy();
    expect(first.getByText('Counter-Strike 2')).toBeTruthy();
    expect(rows[0].textContent).toContain('Deleted 2 hours ago');
    expect(rows[0].textContent).toContain('Deleted for good in 22 hours');
  });

  it('states the retention rule from the push, not from a constant', () => {
    renderTrash([ranked], 72);
    expect(screen.getByTestId('trash-retention').textContent).toBe(
      'Items are kept for 3 days, then deleted for good.',
    );
  });

  it('says what would put something here when the trash is empty', () => {
    renderTrash([]);
    // The title says it is empty; the body says what would put something here, and does not spend
    // its first sentence repeating the title.
    const empty = screen.getByTestId('trash-empty').textContent ?? '';
    expect(empty).toContain('Trash is empty');
    expect(empty).toContain('land here first');
    expect(empty).not.toContain('The trash is empty.');
    expect(screen.queryByTestId('trash-list')).toBeNull();
    expect(screen.queryByTestId('trash-toolbar')).toBeNull();
  });
});

describe('TrashList restore', () => {
  it('restores one entry without a confirmation — it undoes something', () => {
    const { trash } = renderTrash([ranked, shot]);
    fireEvent.click(screen.getByRole('button', { name: 'Restore Ranked win' }));
    expect(screen.queryByTestId('confirm-delete')).toBeNull();
    expect(trash.restore).toHaveBeenCalledWith(['e1']);
  });

  it('restores the whole selection', () => {
    const { trash } = renderTrash([ranked, shot]);
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(screen.getByTestId('trash-selection-count').textContent).toBe('2 selected');

    fireEvent.click(within(screen.getByTestId('trash-toolbar')).getByRole('button', { name: 'Restore' }));
    expect(trash.restore).toHaveBeenCalledWith(['e1', 'e2']);
    expect(screen.getByTestId('trash-selection-count').textContent).toBe('0 selected');
  });
});

describe('TrashList permanent removal', () => {
  it('confirms before purging one entry, and cancels without sending anything', () => {
    const { trash } = renderTrash([ranked, shot]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win permanently' }));

    const dialog = screen.getByTestId('confirm-delete');
    // Already in the trash: there is no trash path left to offer.
    expect(within(dialog).queryByRole('checkbox')).toBeNull();
    expect(within(dialog).getByTestId('confirm-delete-notice').textContent).toContain(
      'cannot be undone',
    );

    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(trash.purge).not.toHaveBeenCalled();
    expect(screen.queryByTestId('confirm-delete')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win permanently' }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(trash.purge).toHaveBeenCalledWith(['e1']);
  });

  it('purges a selection in one command', () => {
    const { trash } = renderTrash([ranked, shot]);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Nice shot' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete permanently' }));

    expect(screen.getByRole('dialog', { name: 'Delete "Nice shot" for good?' })).toBeTruthy();
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(trash.purge).toHaveBeenCalledWith(['e2']);
  });

  it('empties the whole trash behind the same confirmation, naming what goes', () => {
    const { trash } = renderTrash([ranked, shot]);
    fireEvent.click(screen.getByRole('button', { name: 'Empty trash' }));

    const dialog = screen.getByRole('dialog', { name: 'Empty the trash?' });
    expect(within(dialog).getByTestId('confirm-delete-items').textContent).toContain('Ranked win');
    expect(within(dialog).getByTestId('confirm-delete-notice').textContent).toContain('2 items');

    fireEvent.click(within(dialog).getByRole('button', { name: 'Empty trash' }));
    expect(trash.emptyTrash).toHaveBeenCalledTimes(1);
    // The whole-trash command carries no entry ids — that is what makes it "empty everything".
    expect(trash.purge).not.toHaveBeenCalled();
  });
});

describe('TrashList selection', () => {
  it('survives a re-render but drops entries the push no longer carries', () => {
    const trash = controller([ranked, shot]);
    const view = render(<TrashList trash={trash} nowSeconds={NOW} />);
    fireEvent.click(screen.getByRole('button', { name: 'Select all' }));
    expect(screen.getByTestId('trash-selection-count').textContent).toBe('2 selected');

    // A restore elsewhere took `shot` out of the trash. The count must follow it out.
    view.rerender(<TrashList trash={controller([ranked])} nowSeconds={NOW} />);
    expect(screen.getByTestId('trash-selection-count').textContent).toBe('1 selected');
    expect((screen.getByRole('checkbox', { name: 'Select Ranked win' }) as HTMLInputElement).checked).toBe(
      true,
    );
  });

  it('disables the selection-scoped actions while nothing is selected', () => {
    renderTrash([ranked]);
    const toolbar = within(screen.getByTestId('trash-toolbar'));
    expect(toolbar.getByRole('button', { name: 'Restore' })).toHaveProperty('disabled', true);
    expect(toolbar.getByRole('button', { name: 'Delete permanently' })).toHaveProperty(
      'disabled',
      true,
    );
    // Emptying the trash is not aimed at a selection, so it stays live.
    expect(toolbar.getByRole('button', { name: 'Empty trash' })).toHaveProperty('disabled', false);
  });
});
