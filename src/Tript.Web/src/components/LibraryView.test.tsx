// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { LibraryView } from './LibraryView';
import type { ContentItem, StorageStatusMessage } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';
import type { TrashController } from './trash/useTrash';

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

const highlight = item({
  contentType: 'highlight',
  fileName: 'highlight-1.mp4',
  filePath: 'clips/highlight-1.mp4',
  title: 'Best moment',
  favorite: true,
});

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
    expect(within(card).getByText('Recording')).toBeTruthy();
    expect(within(card).getByText('Counter-Strike 2')).toBeTruthy();
    expect(within(card).getByText('30:30')).toBeTruthy();
    expect(within(card).getByText('1.4 GB')).toBeTruthy();

    const clipCard = screen.getByRole('button', { name: 'Open Nice shot' });
    expect(within(clipCard).getByText('Clip')).toBeTruthy();
  });

  it('renders an item with no metadata at all, with honest fallbacks', () => {
    renderLibrary([bare]);
    const card = screen.getByRole('button', { name: 'Open session-bare.mp4' });
    expect(within(card).getByText('Unknown game')).toBeTruthy();
    expect(within(card).getByText('No date')).toBeTruthy();
  });

  it('loads exactly the first recent thumbnail eagerly and deprioritizes the rest', () => {
    const secondRecent = item({
      fileName: 'second-recent.mp4',
      title: 'Second recent session',
      startTime: NOW - 4 * HOUR,
    });
    renderLibrary([session, secondRecent, clip]);
    const images = screen.getAllByRole('presentation') as HTMLImageElement[];
    expect(images[0].getAttribute('src')).toBe('http://localhost:8893/api/thumbnail/sessions/cs2.mp4');
    expect(images.filter((image) => image.getAttribute('loading') === 'eager')).toHaveLength(1);
    expect(images[0].getAttribute('fetchpriority')).toBe('high');
    expect(images.slice(1).every((image) => image.getAttribute('loading') === 'lazy')).toBe(true);
    expect(images.slice(1).every((image) => image.getAttribute('fetchpriority') === 'low')).toBe(true);
  });

  it('falls back to the placeholder tile when the thumbnail request fails or 204s', () => {
    renderLibrary([session]);
    expect(screen.queryByTestId('content-card-placeholder')).toBeNull();

    fireEvent.error(screen.getByRole('presentation'));

    expect(screen.getByTestId('content-card-placeholder')).toBeTruthy();
    expect(screen.queryByRole('presentation')).toBeNull();
    expect(screen.getByRole('button', { name: 'Open Ranked win' })).toBeTruthy();
  });

  it('renders a placeholder rather than requesting a thumbnail for a pathless item', () => {
    renderLibrary([item({ fileName: 'ghost.mp4', filePath: '' })]);
    expect(screen.getByTestId('content-card-placeholder')).toBeTruthy();
    expect(screen.queryByRole('presentation')).toBeNull();
  });

  it('supplies only exact-path automatic highlights in timeline order for missing video', () => {
    const missing = item({
      fileName: 'missing.mp4',
      title: 'Missing session',
      videoMissing: true,
      favorite: true,
    });
    const highlight = (name: string, clipStartTime: number, overrides: Partial<ContentItem> = {}) => item({
      contentType: 'clip',
      fileName: `${name}.mp4`,
      filePath: `clips/${name}.mp4`,
      automated: true,
      sourceSessionPath: missing.filePath,
      clipStartTime,
      ...overrides,
    });
    renderLibrary([
      missing,
      highlight('first', 10),
      highlight('second', 20),
      highlight('manual', 5, { automated: false }),
      highlight('other-source', 1, { sourceSessionPath: 'sessions/other.mp4' }),
    ]);

    const card = screen.getByRole('button', { name: 'Open Missing session' });
    const images = within(card).getAllByRole('presentation') as HTMLImageElement[];
    expect(images.map((image) => image.getAttribute('src'))).toEqual([
      'http://localhost:8893/api/thumbnail/clips/first.mp4',
      'http://localhost:8893/api/thumbnail/clips/second.mp4',
    ]);
    expect(within(card).getByText('Highlights only')).toBeTruthy();
  });

  it('shows the missing-video fallback without requesting the recording thumbnail', () => {
    renderLibrary([item({ fileName: 'gone.mp4', title: 'Gone recording', videoMissing: true })]);
    const card = screen.getByRole('button', { name: 'Open Gone recording' });

    expect(within(card).getByText('Source video unavailable')).toBeTruthy();
    expect(within(card).queryByRole('presentation')).toBeNull();
  });

  it('renders a highlights-only parent with exact linked previews and no favorite action', () => {
    const parent = item({
      fileName: 'highlights-only.mp4',
      title: 'Highlights session',
      highlightsOnly: true,
      favorite: true,
    });
    const linked = item({
      contentType: 'clip',
      fileName: 'linked.mp4',
      filePath: 'clips/linked.mp4',
      automated: true,
      sourceSessionPath: parent.filePath,
    });
    renderLibrary([parent, linked]);

    const card = screen.getByRole('button', { name: 'Open Highlights session' });
    expect(within(card).getAllByText('Highlights-only session')).toHaveLength(2);
    expect(within(card).queryByText('Source video unavailable')).toBeNull();
    expect(within(card).getByRole('presentation').getAttribute('src')).toBe(
      'http://localhost:8893/api/thumbnail/clips/linked.mp4',
    );
    expect(screen.queryByRole('button', { name: /Highlights session .*favorites/i })).toBeNull();
  });

  it('opens the item through the shell seam when a card is activated', () => {
    const onOpen = vi.fn();
    renderLibrary([session, clip], onOpen);
    fireEvent.click(screen.getByRole('button', { name: 'Open Nice shot' }));
    expect(onOpen).toHaveBeenCalledWith(clip, [session, clip], 'library', expect.any(Function));
    const rebuild = onOpen.mock.calls[0][3] as (items: ContentItem[]) => ContentItem[];
    const added = { ...clip, fileName: 'added.mp4', filePath: 'clips/added.mp4', title: 'Added later' };
    expect(rebuild([session, clip, added]).map((item) => item.filePath)).toContain('clips/added.mp4');
  });
});

describe('LibraryView filters and sorting', () => {
  it('filters by type', () => {
    renderLibrary([session, clip, highlight]);
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
    expect(cardTitles()).toEqual(['Nice shot']);
    expect(screen.getByRole('radio', { name: 'Clips', checked: true })).toBeTruthy();

    fireEvent.click(screen.getByRole('radio', { name: 'Highlights' }));
    expect(cardTitles()).toEqual(['Best moment']);
    fireEvent.click(screen.getByLabelText('Favourites only'));
    expect(cardTitles()).toEqual(['Best moment']);
    fireEvent.click(screen.getByLabelText('Favourites only'));

    fireEvent.click(screen.getByRole('radio', { name: 'Sessions' }));
    expect(cardTitles()).toEqual(['Ranked win']);

    fireEvent.click(screen.getByRole('radio', { name: 'All' }));
    expect(cardTitles()).toHaveLength(3);
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
    const view = render(
      <LibraryView client={mockClient()} items={[session, clip]} nowSeconds={NOW} />,
    );
    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'Rocket League' } });
    expect(cardTitles()).toEqual(['Nice shot']);

    view.rerender(<LibraryView client={mockClient()} items={[session]} nowSeconds={NOW} />);

    const games = screen.getByLabelText('Game') as HTMLSelectElement;
    expect(games.value).toBe('Rocket League');
    expect([...games.options].map((option) => option.label)).toContain('Rocket League');
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
    expect(cardTitles()).toEqual(['Ranked win', 'Nice shot']);

    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'game' } });
    expect(cardTitles()).toEqual(['Ranked win', 'Nice shot']);
  });

  it('clears every filter but keeps the sort', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'oldest' } });
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'nice' } });

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));

    expect(cardTitles()).toEqual(['Ranked win', 'Nice shot']);
    expect((screen.getByLabelText('Sort') as HTMLSelectElement).value).toBe('oldest');
    expect((screen.getByLabelText('Search') as HTMLInputElement).value).toBe('');
    expect(screen.getByRole('radio', { name: 'All', checked: true })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Clear filters' })).toBeNull();
  });
});

describe('LibraryView pagination', () => {
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
    expect(screen.getAllByTestId('content-card')).toHaveLength(15);
    expect(screen.getByTestId('library-page').textContent).toBe('Page 1 of 2');
    expect(screen.getByTestId('library-range').textContent).toBe('25 sessions · 25 items');
    expect(screen.getByRole('button', { name: 'Previous page' })).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 2 of 2');
    expect(screen.getByTestId('library-range').textContent).toBe('25 sessions · 25 items');

    expect(screen.getAllByTestId('content-card')).toHaveLength(10);
    expect(screen.getByRole('button', { name: 'Next page' })).toHaveProperty('disabled', true);

    fireEvent.click(screen.getByRole('button', { name: 'Previous page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 1 of 2');
  });

  it('hides the pagination controls when everything fits on one page', () => {
    renderLibrary([session, clip]);
    expect(screen.queryByTestId('library-page')).toBeNull();
  });

  it('resets to page 1 on a filter change, so no page can be left behind', () => {
    renderLibrary(many);
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    expect(screen.getByTestId('library-page').textContent).toBe('Page 2 of 2');

    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'Counter-Strike 2' } });
    expect(screen.getAllByTestId('content-card')).toHaveLength(3);
    expect(screen.queryByTestId('library-page')).toBeNull();
    expect(screen.getByTestId('library-range').textContent).toContain('3 sessions · 3 items');
  });

  it('resets to page 1 on a sort change, since the front of the list changed', () => {
    renderLibrary(many);
    fireEvent.click(screen.getByRole('button', { name: 'Next page' }));
    fireEvent.change(screen.getByLabelText('Sort'), { target: { value: 'oldest' } });
    expect(screen.getByTestId('library-page').textContent).toBe('Page 1 of 2');
    expect(cardTitles()[0]).toBe('Session 24');
  });
});

describe('LibraryView empty states', () => {
  it('says the library is empty when there is no content at all', () => {
    renderLibrary([]);
    const empty = screen.getByTestId('library-empty').textContent ?? '';
    expect(empty).toContain('Nothing recorded yet');
    expect(empty).not.toMatch(/add a game/i);
    expect(screen.getByRole('button', { name: 'Record now' })).toBeTruthy();
    expect(screen.queryByTestId('library-empty-filtered')).toBeNull();
    expect(screen.queryByTestId('library-grid')).toBeNull();
  });

  it('distinguishes a filtered-out grid, and offers to clear the filters', () => {
    renderLibrary([session, clip]);
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'nothing matches this' } });

    const empty = screen.getByTestId('library-empty-filtered');
    expect(empty.textContent).toContain('None of your 2 items matches these filters');
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

  function renderWith(
    items: ContentItem[],
    retentionHours = 24,
    trash?: Partial<TrashController>,
    deleteLinkedHighlightsByDefault = false,
  ) {
    const { client, sent } = recordingClient();
    const controller: TrashController = {
      entries: [],
      retentionHours,
      loaded: true,
      restore: vi.fn(),
      purge: vi.fn(),
      emptyTrash: vi.fn(),
      ...trash,
    };
    const view = render(
      <LibraryView
        client={client}
        items={items}
        nowSeconds={NOW}
        retentionHours={retentionHours}
        deleteLinkedHighlightsByDefault={deleteLinkedHighlightsByDefault}
        trash={controller}
      />,
    );
    return { sent, view, client, trash: controller };
  }

  it('lists trashed items when the type filter is Trash, and only those', () => {
    renderWith([session, clip], 24, {
      entries: [
        {
          id: 't1',
          contentType: 'recording',
          fileName: 'deleted.mp4',
          title: 'Deleted run',
          deletedAt: NOW - 60,
          purgeAt: NOW + 3600,
        },
      ],
    });

    fireEvent.click(screen.getByRole('radio', { name: 'Trash (1)' }));
    expect(screen.getByText('Deleted run')).toBeTruthy();
    expect(screen.queryByText('Ranked win')).toBeNull();
  });

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

  it('combines permanent and linked-highlight choices for a recording', () => {
    const { sent } = renderWith([session]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    fireEvent.click(screen.getByRole('checkbox', {
      name: 'Delete linked highlights (favourited highlights are kept)',
    }));
    fireEvent.click(screen.getByRole('checkbox', { name: /skip trash/i }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent).toEqual([{
      method: 'DeleteContent',
      parameters: {
        contentType: 'recording',
        fileName: 'sessions/cs2.mp4',
        deleteLinkedHighlights: true,
        permanent: true,
      },
    }]);
  });

  it('defaults an absent or false linked-highlight setting unchecked and omits the flag', () => {
    const absent = renderWith([session]);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    expect((screen.getByRole('checkbox', { name: /delete linked highlights/i }) as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(absent.sent[0]).toEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'recording', fileName: 'sessions/cs2.mp4' },
    });

    absent.view.unmount();
    const explicitFalse = renderWith([session], 24, undefined, false);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    expect((screen.getByRole('checkbox', { name: /delete linked highlights/i }) as HTMLInputElement).checked).toBe(false);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(explicitFalse.sent[0]).toEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'recording', fileName: 'sessions/cs2.mp4' },
    });
  });

  it('allows an unchecked missing-video placeholder deletion without a cascade flag', () => {
    const placeholder = {
      ...session,
      fileName: 'missing.mp4',
      filePath: 'sessions/missing.mp4',
      title: 'Missing session',
      videoMissing: true,
    };
    const { sent } = renderWith([placeholder], 24, undefined, true);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Missing session' }));

    const linked = screen.getByRole('checkbox', { name: /delete linked highlights/i });
    expect((linked as HTMLInputElement).checked).toBe(true);
    fireEvent.click(linked);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent).toEqual([{
      method: 'DeleteContent',
      parameters: { contentType: 'recording', fileName: 'sessions/missing.mp4' },
    }]);
  });

  it('excludes favourited highlights from the trash count, matching the cascade', () => {
    const favourite = {
      contentType: 'clip' as const,
      fileName: 'highlight-fav.mp4',
      filePath: 'clips/highlight-fav.mp4',
      title: 'Favourite highlight',
      automated: true,
      favorite: true,
      sourceSessionPath: 'sessions/cs2.mp4',
    };
    const highlight = {
      contentType: 'clip' as const,
      fileName: 'highlight-1.mp4',
      filePath: 'clips/highlight-1.mp4',
      title: 'Normal highlight',
      automated: true,
      favorite: false,
      sourceSessionPath: 'sessions/cs2.mp4',
    };
    renderWith([session, favourite, highlight], 24, undefined, false);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));

    const linked = screen.getByRole('checkbox', { name: /delete linked highlights/i });
    fireEvent.click(linked);
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('2 items');
  });

  it('counts a highlights-only parent as zero files while retaining its highlight cascade', () => {
    const parent = {
      ...session,
      fileName: 'highlights-only.mp4',
      filePath: 'sessions/highlights-only.mp4',
      title: 'Highlights session',
      highlightsOnly: true,
    };
    const highlight = {
      ...clip,
      automated: true,
      sourceSessionPath: parent.filePath,
    };
    renderWith([parent, highlight], 24, undefined, false);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Highlights session' }));

    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('0 items');
    fireEvent.click(screen.getByRole('checkbox', { name: /delete linked highlights/i }));
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('1 item');
  });

  it('does not offer or send the linked-highlight choice for clip-only deletion', () => {
    const { sent } = renderWith([clip], 24, undefined, true);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Nice shot' }));
    expect(screen.queryByRole('checkbox', { name: /delete linked highlights/i })).toBeNull();
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent[0]).toEqual({
      method: 'DeleteContent',
      parameters: { contentType: 'clip', fileName: 'clips/clip-1.mp4' },
    });
  });

  it('quotes the retention the shell was pushed, not a hardcoded 24 hours', () => {
    renderWith([session], 72);
    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    expect(screen.getByTestId('confirm-delete-notice').textContent).toContain('for the next 3 days');
  });

  it('offers no card checkboxes until selection mode is entered, and drops them again on Done', () => {
    const cardCheckboxes = () => document.querySelectorAll('.content-card-select');
    renderWith([session, clip]);
    expect(cardCheckboxes()).toHaveLength(0);

    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    expect(cardCheckboxes()).toHaveLength(2);
    expect(screen.getByTestId('library-selection-count').textContent).toBe('0 selected');

    fireEvent.click(screen.getByRole('button', { name: 'Done' }));
    expect(cardCheckboxes()).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Select' })).toBeTruthy();
  });

  it('selects the whole page and clears it again', () => {
    renderWith([session, clip]);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));

    fireEvent.click(screen.getByRole('button', { name: 'Select page' }));
    expect(screen.getByTestId('library-selection-count').textContent).toBe('2 selected');
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
    expect(screen.getByTestId('library-selection-count').textContent).toBe('0 selected');
  });

  it('applies one bulk linked-highlight choice only to recording targets', () => {
    const { sent } = renderWith([session, clip], 24, undefined, true);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Ranked win' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Nice shot' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete 2' }));
    expect((screen.getByRole('checkbox', {
      name: 'Delete linked highlights (favourited highlights are kept)',
    }) as HTMLInputElement).checked).toBe(true);
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent[0]).toEqual({
      method: 'DeleteMultipleContent',
      parameters: {
        items: [
          {
            contentType: 'recording',
            fileName: 'sessions/cs2.mp4',
            deleteLinkedHighlights: true,
          },
          { contentType: 'clip', fileName: 'clips/clip-1.mp4' },
        ],
      },
    });
  });

  it('removes the cascade flag from every bulk target when the true default is unchecked', () => {
    const { sent } = renderWith([session, clip], 24, undefined, true);
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));
    fireEvent.click(screen.getByRole('button', { name: 'Select page' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete 2' }));
    fireEvent.click(screen.getByRole('checkbox', { name: /delete linked highlights/i }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));

    expect(sent[0]).toEqual({
      method: 'DeleteMultipleContent',
      parameters: {
        items: [
          { contentType: 'recording', fileName: 'sessions/cs2.mp4' },
          { contentType: 'clip', fileName: 'clips/clip-1.mp4' },
        ],
      },
    });
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

    view.rerender(<LibraryView client={client} items={[session]} nowSeconds={NOW} />);
    expect(screen.getByTestId('library-selection-count').textContent).toBe('1 selected');
    expect(
      (screen.getByRole('checkbox', { name: 'Select Ranked win' }) as HTMLInputElement).checked,
    ).toBe(true);
  });

  it('only selects what the filters still show, so a hidden item cannot be deleted by "Select page"', () => {
    const { sent } = renderWith([session, clip]);
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
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

describe('LibraryView recent sessions and groups', () => {
  const newest = item({
    fileName: 'ow-2.mp4',
    title: 'Recording 2',
    game: 'Overwatch',
    startTime: NOW,
  });
  const older = item({
    fileName: 'ow-1.mp4',
    title: 'Recording 1',
    game: 'Overwatch',
    startTime: NOW - DAY,
  });
  const cutOfNewest = item({
    contentType: 'clip',
    fileName: 'ow-2-01.mp4',
    filePath: 'clips/ow-2-01.mp4',
    title: 'Recording 2 - 01',
    startTime: NOW,
  });

  it('makes the newest sessions a recent shelf without a separate highlights action', () => {
    renderLibrary([older, newest, cutOfNewest]);
    const recent = screen.getByTestId('library-recent');
    expect(within(recent).getByRole('heading', { name: 'Recent sessions' })).toBeTruthy();
    expect(within(recent).queryByRole('button', { name: /Highlights/ })).toBeNull();
    expect(within(recent).queryByText('Recording 2 - 01')).toBeNull();
    expect(within(recent).getByText('Clips: 1')).toBeTruthy();
    expect(within(recent).getByText('Recording 1')).toBeTruthy();
  });

  it('renders a recent session without a clips row when it has none', () => {
    renderLibrary([newest]);
    const recent = screen.getByTestId('library-recent');
    expect(within(recent).queryByRole('button', { name: /Highlights/ })).toBeNull();
    expect(within(recent).queryByTestId('recording-group-clips')).toBeNull();
  });

  it('keeps every card affordance on recent sessions', () => {
    renderLibrary([newest]);
    const recent = screen.getByTestId('library-recent');
    expect(within(recent).getByRole('button', { name: 'Delete Recording 2' })).toBeTruthy();
    expect(within(recent).getByRole('button', { name: /Add Recording 2 to favorites/ })).toBeTruthy();
    expect(within(recent).getByRole('button', { name: 'Open Recording 2' })).toBeTruthy();
  });

  it('keeps the recording open affordance separate from Highlights', () => {
    const onOpen = vi.fn();
    renderLibrary([newest], onOpen);
    fireEvent.click(within(screen.getByTestId('library-recent')).getByRole('button', { name: 'Open Recording 2' }));
    expect(onOpen).toHaveBeenCalledWith(expect.objectContaining({ fileName: 'ow-2.mp4' }), expect.anything(), 'session', expect.any(Function));
  });

  it('withdraws the recent shelf once the user is filtering', () => {
    renderLibrary([older, newest, cutOfNewest]);
    expect(screen.getByTestId('library-recent')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'Recording' } });
    expect(screen.queryByTestId('library-recent')).toBeNull();
    expect(screen.getByTestId('library-latest')).toBeTruthy();
  });

  it('falls back to the flat grid when one kind is asked for by name', () => {
    renderLibrary([older, newest, cutOfNewest]);
    fireEvent.click(screen.getByRole('radio', { name: 'Clips' }));
    expect(screen.getByTestId('library-grid')).toBeTruthy();
    expect(screen.queryByTestId('library-hero')).toBeNull();
  });
});

describe('LibraryView sessions surface', () => {
  it('keeps placeholder sessions off the item grids, where the library lists playable items', () => {
    renderLibrary([
      item({ fileName: 'a.mp4', title: 'Session A', startTime: NOW - 1 * HOUR }),
      item({ fileName: 'b.mp4', title: 'Session B', startTime: NOW - 2 * HOUR }),
      item({ fileName: 'c.mp4', title: 'Session C', startTime: NOW - 3 * HOUR }),
      item({ fileName: 'd.mp4', title: 'Session D', startTime: NOW - 4 * HOUR, videoMissing: true }),
    ]);
    const recent = screen.getByTestId('library-recent');
    expect(within(recent).getByRole('button', { name: 'Open Session A' })).toBeTruthy();
    expect(within(recent).queryByRole('button', { name: 'Open Session D' })).toBeNull();
    expect(screen.queryByTestId('library-latest')).toBeNull();
  });

  it('keeps placeholders out of the Sessions list', () => {
    const placeholder = item({ fileName: 'gone.mp4', title: 'Gone session', videoMissing: true });
    renderLibrary([placeholder, session]);
    fireEvent.click(screen.getByRole('radio', { name: 'Sessions' }));
    expect(cardTitles()).toEqual(['Ranked win']);
  });
});

describe('the free space chip', () => {
  const free: StorageStatusMessage = {
    pressure: 'ok',
    freeBytes: 128 * 1024 * 1024 * 1024,
    totalBytes: 1024 * 1024 * 1024 * 1024,
    minimumFreeBytes: 20 * 1024 * 1024 * 1024,
    warnFreeBytes: 60 * 1024 * 1024 * 1024,
    recordingBlocked: false,
    policyConfirmed: true,
    whenFull: 'PauseRecording',
    keepSharingWhenFull: true,
    volumeRoot: 'T:',
    root: 'T:/Tript',
    scratchFreeBytes: 0,
    scratchLow: false,
  };

  function renderWithStorage(onOpenStorageSettings = vi.fn()) {
    render(
      <LibraryView
        client={mockClient()}
        items={[session]}
        nowSeconds={NOW}
        storageStatus={free}
        onOpenStorageSettings={onOpenStorageSettings}
      />,
    );
    return onOpenStorageSettings;
  }

  it('sits in the same row as the Select button', () => {
    renderWithStorage();
    const row = screen.getByTestId('library-selection');

    expect(within(row).getByRole('button', { name: 'Select' })).toBeTruthy();
    expect(within(row).getByTestId('storage-free-chip').textContent).toContain('128 GB free');
  });

  it('draws nothing above the toolbar any more', () => {
    renderWithStorage();
    expect(screen.queryByTestId('storage-bar')).toBeNull();
  });

  it('gets out of the way while picking items', () => {
    renderWithStorage();
    fireEvent.click(screen.getByRole('button', { name: 'Select' }));

    expect(screen.queryByTestId('storage-free-chip')).toBeNull();
  });

  it('stays away when the caller wires up no storage settings', () => {
    render(<LibraryView client={mockClient()} items={[session]} nowSeconds={NOW} storageStatus={free} />);
    expect(screen.queryByTestId('storage-free-chip')).toBeNull();
  });
});
