// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import type { ContentItem } from '../../ipc/protocol';
import { captureSessionToken } from '../../ipc/sessionToken';
import { ContentCard } from './ContentCard';

const normal: ContentItem = {
  contentType: 'clip',
  fileName: 'normal.mp4',
  filePath: 'clips/normal.mp4',
  title: 'Normal clip',
};

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

const highlightsOnly: ContentItem = {
  contentType: 'recording',
  fileName: 'highlights-only.mp4',
  filePath: 'sessions/highlights-only.mp4',
  title: 'Highlights session',
  favorite: true,
  highlightsOnly: true,
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

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  captureSessionToken('');
});

describe('ContentCard thumbnail retries', () => {
  it('keeps retrying a normal thumbnail with capped, cache-busting delays while mounted', () => {
    vi.useFakeTimers();
    captureSessionToken('?k=card-token');
    render(<ContentCard item={normal} />);

    let image = screen.getByRole('presentation') as HTMLImageElement;
    expect(new URL(image.src).searchParams.get('k')).toBe('card-token');
    expect(new URL(image.src).searchParams.has('thumbnailRetry')).toBe(false);

    const delays: number[] = [];
    for (let retry = 1; retry <= 25; retry += 1) {
      fireEvent.error(image);
      expect(screen.getByTestId('content-card-placeholder')).toBeTruthy();
      const retryStartedAt = Date.now();
      act(() => vi.runOnlyPendingTimers());
      delays.push(Date.now() - retryStartedAt);
      image = screen.getByRole('presentation') as HTMLImageElement;
      const retryUrl = new URL(image.src);
      expect(retryUrl.searchParams.get('k')).toBe('card-token');
      expect(retryUrl.searchParams.get('thumbnailRetry')).toBe(String(retry));
    }

    const stagger = delays[0] - 250;
    expect(delays).toHaveLength(25);
    expect(delays).toEqual(
      Array.from({ length: 25 }, (_, attempt) => Math.min(250 * 2 ** attempt, 5_000) + stagger),
    );
    expect(stagger).toBeGreaterThanOrEqual(0);
    expect(stagger).toBeLessThan(100);
    expect(new URL(image.src).searchParams.get('thumbnailRetry')).toBe('25');
    expect(vi.getTimerCount()).toBe(0);
  });

  it('cancels and resets retries when thumbnails are disabled, re-enabled, or the path changes', () => {
    vi.useFakeTimers();
    const view = render(<ContentCard item={normal} />);
    fireEvent.error(screen.getByRole('presentation'));
    expect(vi.getTimerCount()).toBe(1);

    view.rerender(<ContentCard item={normal} thumbnailLoadingActive={false} />);
    expect(vi.getTimerCount()).toBe(0);
    act(() => vi.runOnlyPendingTimers());
    expect(screen.queryByRole('presentation')).toBeNull();

    view.rerender(<ContentCard item={normal} />);
    let image = screen.getByRole('presentation') as HTMLImageElement;
    expect(new URL(image.src).searchParams.has('thumbnailRetry')).toBe(false);
    fireEvent.error(image);
    act(() => vi.runOnlyPendingTimers());

    const replacement = { ...normal, filePath: 'clips/replacement.mp4' };
    view.rerender(<ContentCard item={replacement} />);
    image = screen.getByRole('presentation') as HTMLImageElement;
    expect(image.src).toContain('/api/thumbnail/clips/replacement.mp4');
    expect(new URL(image.src).searchParams.has('thumbnailRetry')).toBe(false);
  });

  it('retries both missing-video and live-recording preview images', () => {
    vi.useFakeTimers();
    render(
      <>
        <ContentCard item={missing} previewHighlights={[highlight(1)]} />
        <ContentCard item={live} highlightsCount={1} previewHighlights={[highlight(2)]} />
      </>,
    );

    screen.getAllByRole('presentation').forEach((image) => fireEvent.error(image));
    expect(screen.queryByRole('presentation')).toBeNull();
    act(() => vi.runAllTimers());

    const retried = screen.getAllByRole('presentation') as HTMLImageElement[];
    expect(retried).toHaveLength(2);
    expect(retried.map((image) => new URL(image.src).searchParams.get('thumbnailRetry'))).toEqual(['1', '1']);
  });
});

describe('ContentCard missing-video placeholder', () => {
  it('does not create missing or live preview images while thumbnail loading is inactive', () => {
    render(
      <>
        <ContentCard item={missing} previewHighlights={[highlight(1)]} thumbnailLoadingActive={false} />
        <ContentCard
          item={live}
          highlightsCount={1}
          previewHighlights={[highlight(2)]}
          thumbnailLoadingActive={false}
        />
      </>,
    );

    expect(screen.getByTestId('content-card-missing-fallback')).toBeTruthy();
    expect(screen.getByTestId('content-card-recording-fallback')).toBeTruthy();
    expect(screen.queryByRole('presentation')).toBeNull();
  });

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

  it('omits favourite but retains open and delete behavior', () => {
    const onOpen = vi.fn();
    const onDelete = vi.fn();
    const onToggleFavorite = vi.fn();
    render(
      <ContentCard
        item={missing}
        onOpen={onOpen}
        onDelete={onDelete}
        onToggleFavorite={onToggleFavorite}
      />,
    );

    expect(screen.queryByRole('button', { name: /favorites/i })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Open Missing session' }));
    fireEvent.click(screen.getByRole('button', { name: 'Delete Missing session' }));

    expect(onOpen).toHaveBeenCalledWith(missing);
    expect(onDelete).toHaveBeenCalledWith(missing);
    expect(onToggleFavorite).not.toHaveBeenCalled();
  });

  it('offers only the select checkbox in selection mode, so a click cannot land on delete', () => {
    const onDelete = vi.fn();
    const onToggleSelected = vi.fn();
    render(
      <ContentCard
        item={missing}
        onDelete={onDelete}
        selectable
        onToggleSelected={onToggleSelected}
      />,
    );

    expect(screen.queryByRole('button', { name: 'Delete Missing session' })).toBeNull();
    fireEvent.click(screen.getByRole('checkbox', { name: 'Select Missing session' }));

    expect(onToggleSelected).toHaveBeenCalledWith(missing);
    expect(onDelete).not.toHaveBeenCalled();
  });
});

describe('ContentCard highlights-only session', () => {
  it('uses intentional wording and linked previews without offering favorite', () => {
    const onOpen = vi.fn();
    const onToggleFavorite = vi.fn();
    render(
      <ContentCard
        item={highlightsOnly}
        previewHighlights={[highlight(1)]}
        onOpen={onOpen}
        onToggleFavorite={onToggleFavorite}
      />,
    );

    const preview = screen.getByTestId('content-card-highlights-only-preview');
    expect(within(preview).getByText('Highlights-only session')).toBeTruthy();
    expect(within(preview).queryByText('Source video unavailable')).toBeNull();
    expect(within(preview).getByRole('presentation').getAttribute('src')).toBe(
      'http://localhost:8893/api/thumbnail/clips/highlight-1.mp4',
    );
    expect(screen.queryByRole('button', { name: /favorites/i })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Open Highlights session' }));
    expect(onOpen).toHaveBeenCalledWith(highlightsOnly);
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

describe('the size chip', () => {
  const sized: ContentItem = {
    contentType: 'recording',
    fileName: 'sized.mp4',
    filePath: 'sessions/sized.mp4',
    title: 'Sized session',
    fileSizeBytes: 1024 * 1024 * 1024,
  };

  it('reports the file on its own when nothing else is given', () => {
    render(<ContentCard item={sized} />);
    expect(screen.getByText('1 GB')).toBeTruthy();
  });

  it('reports what the whole session costs when that is given', () => {
    render(<ContentCard item={sized} sizeBytes={3 * 1024 * 1024 * 1024} />);
    expect(screen.getByText('3 GB')).toBeTruthy();
    expect(screen.queryByText('1 GB')).toBeNull();
  });

  it('shows no chip at all when the size is unknown', () => {
    render(<ContentCard item={normal} />);
    expect(screen.queryByText(/\d (B|KB|MB|GB)$/)).toBeNull();
  });
});
