// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { SessionsView } from './SessionsView';
import type { ContentItem, StorageStatusMessage } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';

const NOW = 1787011200;
const HOUR = 3600;

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

const placeholder = item({
  fileName: 'gone.mp4',
  title: 'Gone session',
  videoMissing: true,
  startTime: NOW - HOUR,
});

const clip = item({
  contentType: 'clip',
  fileName: 'clip-1.mp4',
  filePath: 'clips/clip-1.mp4',
  title: 'Nice shot',
  startTime: NOW - 3 * HOUR,
});

function renderSessions(items: ContentItem[], onOpen?: (item: ContentItem, resultItems: ContentItem[]) => void) {
  return render(<SessionsView client={mockClient()} items={items} onOpen={onOpen} nowSeconds={NOW} />);
}

afterEach(cleanup);

describe('SessionsView grid', () => {
  it('lists every session, placeholders included, and never a clip', () => {
    renderSessions([placeholder, session, clip]);
    const grid = screen.getByTestId('sessions-grid');
    expect(within(grid).getByRole('button', { name: 'Open Gone session' })).toBeTruthy();
    expect(within(grid).getByRole('button', { name: 'Open Ranked win' })).toBeTruthy();
    expect(within(grid).queryByRole('button', { name: 'Open Nice shot' })).toBeNull();
  });

  it('opens a session through the shell seam with the page result set', () => {
    const onOpen = vi.fn();
    renderSessions([placeholder, session], onOpen);
    fireEvent.click(screen.getByRole('button', { name: 'Open Ranked win' }));
    expect(onOpen).toHaveBeenCalledWith(session, [placeholder, session]);
  });

  it('sends the favorite command from a card', () => {
    const client = { ...mockClient(), send: vi.fn() } as unknown as IpcClient;
    render(<SessionsView client={client} items={[session]} nowSeconds={NOW} />);
    fireEvent.click(screen.getByRole('button', { name: 'Add Ranked win to favorites' }));
    expect(client.send).toHaveBeenCalledWith('ToggleFavorite', {
      contentType: 'recording',
      filePath: 'sessions/cs2.mp4',
      favorite: true,
    });
  });

  it('deletes through the confirmation, honouring the cascade choice', () => {
    const client = { ...mockClient(), send: vi.fn() } as unknown as IpcClient;
    const linked = item({
      contentType: 'highlight',
      fileName: 'linked.mp4',
      filePath: 'clips/linked.mp4',
      automated: true,
      sourceSessionPath: session.filePath,
    });
    render(<SessionsView client={client} items={[session, linked]} nowSeconds={NOW} />);

    fireEvent.click(screen.getByRole('button', { name: 'Delete Ranked win' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Delete linked highlights (favourited highlights are kept)' }));
    fireEvent.click(screen.getByTestId('confirm-delete-confirm'));
    expect(client.send).toHaveBeenCalledWith('DeleteContent', {
      contentType: 'recording',
      fileName: 'sessions/cs2.mp4',
      deleteLinkedHighlights: true,
    });
  });
});

describe('SessionsView empty states', () => {
  it('explains an empty catalogue', () => {
    renderSessions([]);
    expect(screen.getByTestId('sessions-empty')).toBeTruthy();
  });

  it('explains a filtered-out catalogue separately', () => {
    renderSessions([session]);
    fireEvent.change(screen.getByLabelText('Search'), { target: { value: 'no such title' } });
    expect(screen.getByTestId('sessions-empty-filtered')).toBeTruthy();
  });
});

describe('the free space chip in sessions', () => {
  const free: StorageStatusMessage = {
    pressure: 'warning',
    freeBytes: 40 * 1024 * 1024 * 1024,
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

  it('sits in the range row and carries the pressure', () => {
    render(
      <SessionsView
        client={mockClient()}
        items={[session]}
        nowSeconds={NOW}
        storageStatus={free}
        onOpenStorageSettings={vi.fn()}
      />,
    );

    const chip = screen.getByTestId('storage-free-chip');
    expect(chip.textContent).toContain('40 GB free');
    expect(chip.getAttribute('data-pressure')).toBe('warning');
  });

  it('stays away when no storage settings are wired up', () => {
    renderSessions([session]);
    expect(screen.queryByTestId('storage-free-chip')).toBeNull();
  });
});
