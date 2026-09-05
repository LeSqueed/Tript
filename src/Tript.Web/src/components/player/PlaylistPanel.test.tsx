// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
  it('renders only a window of a large playlist and selects a visible item', () => {
    const onSelect = vi.fn();
    render(<PlaylistPanel items={Array.from({ length: 100 }, (_, index) => item(index))} currentIndex={0} onSelect={onSelect} />);

    expect(screen.getAllByRole('button')).toHaveLength(11);
    fireEvent.click(screen.getByRole('button', { name: 'Play Item 5' }));
    expect(onSelect).toHaveBeenCalledWith(5);
    expect(screen.queryByRole('button', { name: 'Play Item 50' })).toBeNull();
  });

  it('starts the rendered window around an item deep in the playlist', () => {
    render(<PlaylistPanel items={Array.from({ length: 100 }, (_, index) => item(index))} currentIndex={50} onSelect={() => {}} />);

    expect(screen.getByRole('button', { name: 'Playing Item 50' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Play Item 0' })).toBeNull();
  });
});
