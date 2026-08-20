// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library grid, rendered. The derivation itself is tested without a DOM
// (library/libraryModel.test.ts); what needs a DOM is the wiring: that each control reaches the
// right dimension of the query, that a filter change resets the page (so the user cannot be left
// looking at a page that no longer exists), that a thumbnail the backend cannot supply degrades to
// the placeholder tile instead of a broken image, and that the two empty states are actually
// distinguishable — the one thing that separates "you have no recordings" from "your filters hide
// all of them", which otherwise looks identical to a broken backend.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { LibraryView } from './LibraryView';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';

/** A fixed "now": 2026-08-17T00:00:00Z in epoch seconds. */
const NOW = 1787011200;
const HOUR = 3600;
const DAY = 24 * HOUR;

function mockClient(): IpcClient {
  return {
    state: 'connected',
    connect: () => {},
    close: () => {},
    send: () => {},
    on: () => () => {},
    onStateChange: () => () => {},
  };
}

function item(overrides: Partial<ContentItem> & { fileName: string }): ContentItem {
  return {
    contentType: 'recording',
    filePath: `sessions/${overrides.fileName}`,
    ...overrides,
  };
}

const session = item({
  fileName: 'cs2.mp4',
  title: 'Ranked win',
  game: 'Counter-Strike 2',
  startTime: NOW - 2 * HOUR,
  durationSeconds: 1830,
  fileSizeBytes: 1_500_000_000,
});

const clip = item({
  contentType: 'clip',
  fileName: 'clip-1.mp4',
  filePath: 'clips/clip-1.mp4',
  title: 'Nice shot',
  game: 'Rocket League',
  startTime: NOW - 3 * DAY,
  durationSeconds: 3.083333,
  fileSizeBytes: 33338,
});

/** No metadata record at all: the shape an un-post-processed recording arrives in. */
const bare = item({ fileName: 'session-bare.mp4' });

function renderLibrary(items: ContentItem[], onOpen?: (item: ContentItem, resultItems: ContentItem[]) => void) {
  return render(
    <LibraryView client={mockClient()} items={items} onOpen={onOpen} nowSeconds={NOW} />,
  );
}

function cardTitles(): string[] {
  return screen
    .getAllByTestId('content-card')
    .map((card) => within(card).getByTitle(/.+/).textContent ?? '');
}

afterEach(cleanup);

describe('LibraryView grid', () => {
  it('exposes favorite state and sends the favorite command from a card', () => {
    const client = { ...mockClient(), send: vi.fn() } as unknown as IpcClient;
    render(<LibraryView client={client} items={[session]} nowSeconds={NOW} />);

    const favorite = screen.getByRole('button', { name: 'Add Ranked win to favorites' });
    expect(favorite.getAttribute('aria-pressed')).toBe('false');
    fireEvent.click(favorite);
    expect(client.send).toHaveBeenCalledWith('ToggleFavorite', {
      contentType: 'recording',
      filePath: 'sessions/cs2.mp4',
      favorite: true,
    });
  });

  it('renders one card per item, with the chips it can fill in', () => {
    renderLibrary([session, clip]);
    expect(screen.getAllByTestId('content-card')).toHaveLength(2);

    const card = screen.getByRole('button', { name: 'Open Ranked win' });
    expect(within(card).getByText('Session')).toBeTruthy();
    expect(within(card).getByText('Counter-Strike 2')).toBeTruthy();
    expect(within(card).getByText('30:30')).toBeTruthy();
    expect(within(card).getByText('1.4 GB')).toBeTruthy();

    const clipCard = screen.getByRole('button', { name: 'Open Nice shot' });
    expect(within(clipCard).getByText('Clip')).toBeTruthy();
  });

  it('renders an item with no metadata at all, with honest fallbacks', () => {
    // The point of the test: a missing game/date/duration/size must cost the item chips, never its
    // place in the grid.
    renderLibrary([bare]);
    const card = screen.getByRole('button', { name: 'Open session-bare.mp4' });
    expect(within(card).getByText('Unknown game')).toBeTruthy();
    expect(within(card).getByText('No date')).toBeTruthy();
  });

  it('lazy-loads thumbnails from the content server', () => {
    renderLibrary([session]);
    const image = screen.getByRole('presentation') as HTMLImageElement;
    expect(image.getAttribute('src')).toBe('http://localhost:2222/api/thumbnail/sessions/cs2.mp4');
    // A library is unbounded: a thousand cards must not become a thousand requests on mount.
    expect(image.getAttribute('loading')).toBe('lazy');
  });

  it('falls back to the placeholder tile when the thumbnail request fails or 204s', () => {
    renderLibrary([session]);
    expect(screen.queryByTestId('content-card-placeholder')).toBeNull();

    // `GET /api/thumbnail/...` answers 204 No Content when there is no thumbnail; an <img> given no
    // image data fires `error`, which is the only signal the element offers.
    fireEvent.error(screen.getByRole('presentation'));

    expect(screen.getByTestId('content-card-placeholder')).toBeTruthy();
    expect(screen.queryByRole('presentation')).toBeNull();
    // The card is still a card: the title and chips never depended on the thumbnail.
    expect(screen.getByRole('button', { name: 'Open Ranked win' })).toBeTruthy();
  });

  it('renders a placeholder rather than requesting a thumbnail for a pathless item', () => {
    renderLibrary([item({ fileName: 'ghost.mp4', filePath: '' })]);
    expect(screen.getByTestId('content-card-placeholder')).toBeTruthy();
    expect(screen.queryByRole('presentation')).toBeNull();
  });

  it('opens the item through the shell seam when a card is activated', () => {
    const onOpen = vi.fn();
    renderLibrary([session, clip], onOpen);
    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    expect(onOpen).toHaveBeenCalledWith(clip, [session, clip]);
  });
});

describe('LibraryView filters and sorting', () => {
  it('filters by type', () => {
    renderLibrary([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Clips' }));
    expect(cardTitles()).toEqual(['Nice shot']);
    expect(screen.getByRole('button', { name: 'Clips', pressed: true })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Sessions' }));
    expect(cardTitles()).toEqual(['Ranked win']);

    fireEvent.click(screen.getByRole('button', { name: 'All' }));
    expect(cardTitles()).toHaveLength(2);
  });

  it('offers only the games that are present, plus an unknown option when something lacks one', () => {
    renderLibrary([session, clip, bare]);
    const games = screen.getByLabelText('Game') as HTMLSelectElement;
    expect([...games.options].map((option) => option.label)).toEqual([
      'All games',
      'Counter-Strike 2',
      'Rocket League',
      'Unknown game',
    ]);

    fireEvent.change(games, { target: { value: 'Rocket League' } });
    expect(cardTitles()).toEqual(['Nice shot']);
  });

  it('does not offer the unknown-game option when every item has a game', () => {
    renderLibrary([session, clip]);
    const games = screen.getByLabelText('Game') as HTMLSelectElement;
    expect([...games.options].map((option) => option.label)).toEqual([
      'All games',
      'Counter-Strike 2',
      'Rocket League',
    ]);
  });

  it('keeps a selected game as an option after a content push removes its last item', () => {
    // A select whose value matches none of its options renders blank, which reads as a broken control
    // rather than as a filter that now matches nothing.
    const view = render(
      <LibraryView client={mockClient()} items={[session, clip]} nowSeconds={NOW} />,
    );
    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'Rocket League' } });
    expect(cardTitles()).toEqual(['Nice shot']);

    view.rerender(<LibraryView client={mockClient()} items={[session]} nowSeconds={NOW} />);

    const games = screen.getByLabelText('Game') as HTMLSelectElement;
    expect(games.value).toBe('Rocket League');
    expect([...games.options].map((option) => option.label)).toContain('Rocket League');
    // And the grid explains itself rather than looking empty for no reason.
    expect(screen.getByTestId('library-empty-filtered')).toBeTruthy();
  });

  it('filters by date window', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Date'), { target: { value: 'day' } });
    expect(cardTitles()).toEqual(['Ranked win']);

    fireEvent.change(screen.getByLabelText('Date'), { target: { value: 'week' } });
    expect(cardTitles()).toHaveLength(2);
  });

  it('filters by free text over the title', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'nice' } });
    expect(cardTitles()).toEqual(['Nice shot']);
  });

  it('sorts by date and by game', () => {
    renderLibrary([session, clip]);
    expect(cardTitles()).toEqual(['Ranked win', 'Nice shot']);

    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'oldest' } });
    expect(cardTitles()).toEqual(['Nice shot', 'Ranked win']);

    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'game' } });
    expect(cardTitles()).toEqual(['Ranked win', 'Nice shot']);
  });

  it('clears every filter but keeps the sort', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'oldest' } });
    fireEvent.click(screen.getByRole('button', { name: 'Clips' }));
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'nice' } });

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));

    expect(cardTitles()).toEqual(['Nice shot', 'Ranked win']);
    expect((screen.getByLabelText('Sort') as HTMLSelectElement).value).toBe('oldest');
    expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('');
    expect(screen.getByRole('button', { name: 'All', pressed: true })).toBeTruthy();
    // The affordance only exists while something is being filtered.
    expect(screen.queryByRole('button', { name: 'Clear filters' })).toBeNull();
  });
});

describe('LibraryView pagination', () => {
  /** 25 dated sessions — three pages at the default size of 12. */
  const many = Array.from({ length: 25 }, (_, index) =>
    item({
      fileName: `s-${index}.mp4`,
      title: `Session ${index}`,
      game: index < 3 ? 'Counter-Strike 2' : 'Rocket League',
      startTime: NOW - index * HOUR,
    }),
  );

  it('pages the grid and reports the position', () => {
    renderLibrary(many);
    expect(screen.getAllByTestId('content-card')).toHaveLength(12);
    expect(screen.getByTestId('library-page').textContent).toBe('Page 1 of 3');
    expect(screen.getByTestId('library-range').textContent).toBe('Showing 1–12 of 25');
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 2 of 3');
    expect(screen.getByTestId('library-range').textContent).toBe('Showing 13–24 of 25');

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 3 of 3');
    expect(screen.getAllByTestId('content-card')).toHaveLength(1);
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 2 of 3');
  });

  it('hides the pagination controls when everything fits on one page', () => {
    renderLibrary([session, clip]);
    expect(screen.queryByTestId('library-page')).toBeNull();
  });

  it('resets to page 1 on a filter change, so no page can be left behind', () => {
    renderLibrary(many);
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 3 of 3');

    // Three items match — page 3 stops existing. The grid must show the matches, not an empty page.
    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'Counter-Strike 2' } });
    expect(screen.getAllByTestId('content-card')).toHaveLength(3);
    expect(screen.queryByTestId('library-page')).toBeNull();
    expect(screen.getByTestId('library-range').textContent).toContain('Showing 1–3 of 3');
  });

  it('resets to page 1 on a sort change, since the front of the list changed', () => {
    renderLibrary(many);
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'oldest' } });
    expect(screen.getByTestId('library-page').textContent).toBe('Page 1 of 3');
    expect(cardTitles()[0]).toBe('Session 24');
  });
});

describe('LibraryView empty states', () => {
  it('says the library is empty when there is no content at all', () => {
    renderLibrary([]);
    expect(screen.getByTestId('library-empty').textContent).toContain('will appear here');
    expect(screen.queryByTestId('library-empty-filtered')).toBeNull();
    expect(screen.queryByTestId('library-grid')).toBeNull();
  });

  it('distinguishes a filtered-out grid, and offers to clear the filters', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'nothing matches this' } });

    const empty = screen.getByTestId('library-empty-filtered');
    expect(empty.textContent).toContain('None of your 2 items matches these filters');
    // This is the whole point of the distinction: a way out is visible.
    fireEvent.click(within(empty).getByRole('button', { name: 'Clear filters' }));
    expect(screen.getAllByTestId('content-card')).toHaveLength(2);
    expect(screen.queryByTestId('library-empty-filtered')).toBeNull();
  });

  it('never shows the filtered empty state when nothing is being filtered', () => {
    renderLibrary([]);
    expect(screen.queryByTestId('library-empty-filtered')).toBeNull();
    expect(screen.getByTestId('library-empty')).toBeTruthy();
  });
});

describe('LibraryView delete and selection', () => {
  /** The commands a mock client saw, so the wire shape can be asserted rather than assumed. */
  function recordingClient(): { client: IpcClient; sent: { method: string; parameters?: unknown }[] } {
    const sent: { method: string; parameters?: unknown }[] = [];
    return {
      sent,
      client: {
        state: 'connected',
        connect: () => {},
        close: () => {},
        send: (method, parameters) => sent.push({ method, parameters }),
        on: () => () => {},
        onStateChange: () => () => {},
      },
    };
  }

  function renderWith(items: ContentItem[], retentionHours = 24) {
    const { client, sent } = recordingClient();
    const view = render(
      <LibraryView client={client} items={items} nowSeconds={NOW} retentionHours={retentionHours} />,
    );
    return { sent, view, client };
  }

  it('confirms a per-item delete before sending anything, and cancels cleanly', () => {
    const { sent } = renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));

    const dialog = screen.getByRole('dialog', { name: 'Delete "Ranked win"?' });
    expect(within(dialog).getByTestId('confirm-delete-notice').textContent).toContain(
      'moved to the trash',
    );
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByTestId('confirm-delete')).toBeNull();
    expect(sent).toHaveLength(0);
  });

  it('sends DeleteContent with no permanent flag for the trash path', () => {
    const { sent } = renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Nice shot' }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent).toEqual([
      // `fileName` is the root-relative path from the item's `filePath`, never the bare file name.
      { method: 'DeleteContent', parameters: { contentType: 'clip', fileName: 'clips/clip-1.mp4' } },
    ]);
  });

  it('sends permanent: true when the skip-trash checkbox is ticked', () => {
    const { sent } = renderWith([session]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    fireEvent.click(screen.getByRole('checkbox', { name: /skip trash/i }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent).toEqual([
      {
        method: 'DeleteContent',
        parameters: { contentType: 'recording', fileName: 'sessions/cs2.mp4', permanent: true },
      },
    ]);
  });

  it('quotes the retention the shell was pushed, not a hardcoded 24 hours', () => {
    renderWith([session], 72);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('for the next 3 days');
  });

  it('offers no checkboxes until selection mode is entered, and drops them again on Done', () => {
    renderWith([session, clip]);
    expect(screen.queryByRole('checkbox')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    expect(screen.getAllByRole('checkbox')).toHaveLength(2);
    expect(screen.getByTestId('library-selection-count').textContent).toBe('0 selected');

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(screen.getByRole('button', { name: 'Select' })).toBeTruthy();
  });

  it('selects the whole page and clears it again', () => {
    renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));

    fireEvent.click(screen.getByRole('button', { name: 'Select page' }));
    expect(screen.getByTestId('library-selection-count').textContent).toBe('2 selected');
    // The affordance flips once the page is covered, so it is never a no-op.
    fireEvent.click(screen.getByRole('button', { name: 'Deselect page' }));
    expect(screen.getByTestId('library-selection-count').textContent).toBe('0 selected');
  });

  it('bulk-deletes the selection through DeleteMultipleContent', () => {
    const { sent } = renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Ranked win' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Nice shot' }));

    fireEvent.click(screen.getByRole('button', { name: 'Delete 2' }));
    expect(screen.getByRole('dialog', { name: 'Delete 2 items?' })).toBeTruthy();
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent).toEqual([
      {
        method: 'DeleteMultipleContent',
        parameters: {
          items: [
            { contentType: 'recording', fileName: 'sessions/cs2.mp4' },
            { contentType: 'clip', fileName: 'clips/clip-1.mp4' },
          ],
        },
      },
    ]);
    // The cards go when the `content` push arrives; the selection must not still claim them.
    expect(screen.getByTestId('library-selection-count').textContent).toBe('0 selected');
  });

  it('cannot bulk-delete nothing', () => {
    renderWith([session]);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    expect(screen.getByRole('button', { name: 'Delete' })).toHaveProperty('disabled', true);
  });

  it('keeps a selection across a content push, but not the items that push removed', () => {
    const { client } = recordingClient();
    const view = render(
      <LibraryView client={client} items={[session, clip]} nowSeconds={NOW} />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    fireEvent.click(screen.getByRole('button', { name: 'Select page' }));
    expect(screen.getByTestId('library-selection-count').textContent).toBe('2 selected');

    // A push after someone else deleted the clip. The selection must follow the list, not lag it.
    view.rerender(<LibraryView client={client} items={[session]} nowSeconds={NOW} />);
    expect(screen.getByTestId('library-selection-count').textContent).toBe('1 selected');
    expect(
      (screen.getByRole('checkbox', { name: 'Select Ranked win' }) as HTMLInputElement).checked,
    ).toBe(true);
  });

  it('only selects what the filters still show, so a hidden item cannot be deleted by "Select page"', () => {
    const { sent } = renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Clips' }));
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    fireEvent.click(screen.getByRole('button', { name: 'Select page' }));
    expect(screen.getByTestId('library-selection-count').textContent).toBe('1 selected');

    fireEvent.click(screen.getByRole('button', { name: 'Delete 1' }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(sent).toEqual([
      { method: 'DeleteContent', parameters: { contentType: 'clip', fileName: 'clips/clip-1.mp4' } },
    ]);
  });
});
