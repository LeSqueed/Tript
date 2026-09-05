// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import { PlaylistPanel, playlistWindow } from './PlaylistPanel';

function item(index: number): ContentItem {
  return {
    contentType: index % 2 === 0 ? 'clip' : 'highlight',
    fileName: `item-${index}.mp4`,
    filePath: `clips/item-${index}.mp4`,
    title: `Item ${index}`,
    durationSeconds: index + 1,
  };
}

afterEach(cleanup);

describe('playlistWindow', () => {
  it('renders a buffered slice around the viewport', () => {
    expect(playlistWindow(100, 0, 164)).toEqual({ start: 0, end: 6 });
    expect(playlistWindow(100, 820, 164)).toEqual({ start: 6, end: 16 });
  });
});

describe('PlaylistPanel', () => {
  function largePlaylist(): ContentItem[] {
    return Array.from({ length: 100 }, (_, index) => item(index));
  }

  it('renders only a window of a large playlist and selects a visible item', () => {
    const onSelect = vi.fn();
    const { container } = render(<PlaylistPanel items={largePlaylist()} currentIndex={0} onSelect={onSelect} />);
    const scroll = container.querySelector('.player-playlist-scroll') as HTMLElement;

    expect(within(scroll).getAllByRole('button')).toHaveLength(11);
    fireEvent.click(screen.getByRole('button', { name: 'Play Item 5' }));
    expect(onSelect).toHaveBeenCalledWith(5);
    expect(screen.queryByRole('button', { name: 'Play Item 50' })).toBeNull();
  });

  it('starts the rendered window around an item deep in the playlist', () => {
    render(<PlaylistPanel items={largePlaylist()} currentIndex={50} onSelect={() => {}} />);

    expect(screen.getByRole('button', { name: 'Playing Item 50' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Play Item 0' })).toBeNull();
  });

  it('re-centres on the playing item from the counter after scrolling away', () => {
    const { container } = render(<PlaylistPanel items={largePlaylist()} currentIndex={50} onSelect={() => {}} />);
    const scroll = container.querySelector('.player-playlist-scroll') as HTMLElement;

    fireEvent.scroll(scroll, { target: { scrollTop: 0 } });
    expect(within(scroll).getByRole('button', { name: 'Play Item 0' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Playing Item 50' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Jump to current item' }));
    expect(within(scroll).getByRole('button', { name: 'Playing Item 50' })).toBeTruthy();
    expect(within(scroll).queryByRole('button', { name: 'Play Item 0' })).toBeNull();
  });
});
