// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { BookmarkPanel, activeBookmarkId } from './BookmarkPanel';
import type { BookmarkItem } from '../../ipc/protocol';

const bookmarks: BookmarkItem[] = [
  { id: 'k1', type: 'kill', time: 20 },
  { id: 'd1', type: 'death', time: 45 },
  { id: 'm1', type: 'manual', time: 80 },
  { id: 'k2', type: 'kill', time: 95 },
];

function renderPanel(overrides: Partial<Parameters<typeof BookmarkPanel>[0]> = {}) {
  const props = {
    bookmarks,
    currentTime: 0,
    hiddenKinds: new Set<string>(),
    onToggleKind: vi.fn(),
    onSeek: vi.fn(),
    onDelete: vi.fn(),
    ...overrides,
  };
  return { ...render(<BookmarkPanel {...props} />), props };
}

afterEach(cleanup);

describe('BookmarkPanel', () => {
  it('lists one filter per type present, with its count', () => {
    renderPanel();
    expect(screen.getByRole('checkbox', { name: 'Show Kill bookmarks' })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Show Death bookmarks' })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Show Manual bookmarks' })).toBeTruthy();
    expect(screen.queryByRole('checkbox', { name: 'Show Goal bookmarks' })).toBeNull();

    const kill = screen.getByRole('checkbox', { name: 'Show Kill bookmarks' });
    expect(kill.closest('.bookmark-filter')?.textContent).toContain('2');
  });

  it('lists the bookmarks in time order', () => {
    const { container } = renderPanel();
    const times = [...container.querySelectorAll('.bookmark-row-time')].map((n) => n.textContent);
    expect(times).toEqual(['0:20', '0:45', '1:20', '1:35']);
  });

  it('hides the rows of a hidden type but keeps its checkbox', () => {
    const { container } = renderPanel({ hiddenKinds: new Set(['kill']) });
    const times = [...container.querySelectorAll('.bookmark-row-time')].map((n) => n.textContent);
    expect(times).toEqual(['0:45', '1:20']);
    expect(screen.getByRole('checkbox', { name: 'Show Kill bookmarks' })).toBeTruthy();
  });

  it('reports the type when a filter is toggled', () => {
    const { props } = renderPanel();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Show Death bookmarks' }));
    expect(props.onToggleKind).toHaveBeenCalledWith('death');
  });

  it('seeks to a bookmark when its row is clicked', () => {
    const { props } = renderPanel();
    fireEvent.click(screen.getByRole('button', { name: 'Play from Death at 0:45' }));
    expect(props.onSeek).toHaveBeenCalledWith(45);
  });

  it('offers a delete only on manual bookmarks', () => {
    const { props } = renderPanel();
    expect(screen.getAllByRole('button', { name: /^Remove the bookmark/ })).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Remove the bookmark at 1:20' }));
    expect(props.onDelete).toHaveBeenCalledWith(bookmarks[2]);
  });

  it('offers no delete when the caller does not supply one', () => {
    renderPanel({ onDelete: undefined });
    expect(screen.queryByRole('button', { name: /^Remove the bookmark/ })).toBeNull();
  });

  it('says so when everything is filtered out, and when there is nothing at all', () => {
    const { unmount } = renderPanel({ hiddenKinds: new Set(['kill', 'death', 'manual']) });
    expect(screen.getByText('Nothing matches the types you picked.')).toBeTruthy();
    unmount();

    renderPanel({ bookmarks: [] });
    expect(screen.getByText('No bookmarks on this recording yet.')).toBeTruthy();
  });
});

describe('activeBookmarkId', () => {
  it('picks the last bookmark at or before the current time', () => {
    expect(activeBookmarkId(bookmarks, 50)).toBe('d1');
    expect(activeBookmarkId(bookmarks, 95)).toBe('k2');
  });

  it('is null before the first bookmark', () => {
    expect(activeBookmarkId(bookmarks, 5)).toBeNull();
  });
});
