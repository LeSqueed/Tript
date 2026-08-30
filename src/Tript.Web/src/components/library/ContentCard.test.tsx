// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { ContentItem } from '../../ipc/protocol';
import { ContentCard } from './ContentCard';

const missing: ContentItem = {
  contentType: 'recording',
  fileName: 'missing.mp4',
  filePath: 'sessions/missing.mp4',
  title: 'Missing session',
  favorite: true,
  videoMissing: true,
};

const live: ContentItem = {
  contentType: 'recording',
  fileName: 'live.mp4',
  filePath: 'sessions/live.mp4',
  title: 'Live session',
  game: 'Counter-Strike 2',
  recording: true,
};

function highlight(index: number): ContentItem {
  return {
    contentType: 'clip',
    fileName: `highlight-${index}.mp4`,
    filePath: `clips/highlight-${index}.mp4`,
    automated: true,
    sourceSessionPath: missing.filePath,
    clipStartTime: index,
  };
}

afterEach(cleanup);

describe('ContentCard missing-video placeholder', () => {
  it('uses at most three supplied highlight thumbnails and keeps its fallback after image failures', () => {
    render(<ContentCard item={missing} previewHighlights={[1, 2, 3, 4].map(highlight)} />);

    const preview = screen.getByTestId('content-card-missing-preview');
    const images = within(preview).getAllByRole('presentation') as HTMLImageElement[];
    expect(images.map((image) => image.getAttribute('src'))).toEqual([
      'http://localhost:8893/api/thumbnail/clips/highlight-1.mp4',
      'http://localhost:8893/api/thumbnail/clips/highlight-2.mp4',
      'http://localhost:8893/api/thumbnail/clips/highlight-3.mp4',
    ]);

    images.forEach((image) => fireEvent.error(image));
    expect(within(preview).getByText('Source video unavailable')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Open Missing session' })).toBeTruthy();
  });

  it('omits favourite but retains open, delete, and select behavior', () => {
    const onOpen = vi.fn();
    const onDelete = vi.fn();
    const onToggleFavorite = vi.fn();
    const onToggleSelected = vi.fn();
    render(
      <ContentCard
        item={missing}
        onOpen={onOpen}
        onDelete={onDelete}
        onToggleFavorite={onToggleFavorite}
        selectable
        onToggleSelected={onToggleSelected}
      />,
    );

    expect(screen.queryByRole('button', { name: /favorites/i })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open Missing session' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete Missing session' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Missing session' }));

    expect(onOpen).toHaveBeenCalledWith(missing);
    expect(onDelete).toHaveBeenCalledWith(missing);
    expect(onToggleSelected).toHaveBeenCalledWith(missing);
    expect(onToggleFavorite).not.toHaveBeenCalled();
  });
});

describe('ContentCard live recording', () => {
  it('presents the session as recording in progress without interactivity until it has highlights', () => {
    const onOpen = vi.fn();
    const onDelete = vi.fn();
    const onToggleFavorite = vi.fn();
    const onToggleSelected = vi.fn();
    render(
      <ContentCard
        item={live}
        onOpen={onOpen}
        onDelete={onDelete}
        onToggleFavorite={onToggleFavorite}
        selectable
        onToggleSelected={onToggleSelected}
      />,
    );

    expect(screen.getByTestId('content-card-recording-fallback')).toBeTruthy();
    expect(screen.getByText('Recording in progress')).toBeTruthy();
    expect(screen.getByText(live.game!)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /favorites/i })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Delete Live session' })).toBeNull();
    expect(screen.queryByRole('checkbox', { name: 'Select Live session' })).toBeNull();

    const open = screen.getByRole('button', { name: /Recording in progress: Live session/i });
    fireEvent.click(open);

    expect(onOpen).not.toHaveBeenCalled();
    expect(onDelete).not.toHaveBeenCalled();
    expect(onToggleSelected).not.toHaveBeenCalled();
  });

  it('opens directly to review once it has highlights, and shows them behind the recording marker', () => {
    const onOpen = vi.fn();
    render(<ContentCard item={live} onOpen={onOpen} highlightsCount={1} previewHighlights={[highlight(1)]} />);

    const preview = screen.getByTestId('content-card-recording-preview');
    const images = within(preview).getAllByRole('presentation') as HTMLImageElement[];
    expect(images.map((image) => image.getAttribute('src'))).toEqual([
      'http://localhost:8893/api/thumbnail/clips/highlight-1.mp4',
    ]);

    fireEvent.click(screen.getByRole('button', { name: 'Open Live session' }));
    expect(onOpen).toHaveBeenCalledWith(live);
  });
});
