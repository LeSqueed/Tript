// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { SessionClipsView } from './SessionClipsView';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';

const recording: ContentItem = {
  contentType: 'recording',
  fileName: 'session.mp4',
  filePath: 'sessions/session.mp4',
  title: 'Ranked session',
};

const highlight: ContentItem = {
  contentType: 'clip',
  fileName: 'highlight.mp4',
  filePath: 'clips/highlight.mp4',
  title: 'Team kill',
  favorite: true,
  automated: true,
  sourceSessionPath: recording.filePath,
};

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

afterEach(cleanup);

describe('SessionClipsView', () => {
  it('offers favorite on each highlight card', () => {
    const onToggleFavorite = vi.fn();
    render(
      <SessionClipsView
        recording={recording}
        clips={[highlight]}
        client={mockClient()}
        onBack={() => {}}
        onOpen={() => {}}
        onToggleFavorite={onToggleFavorite}
        onDelete={() => {}}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Remove Team kill from favorites' }));
    expect(onToggleFavorite).toHaveBeenCalledWith(highlight);
  });

  it('routes card deletion through its confirmation owner', () => {
    const onDelete = vi.fn();
    render(
      <SessionClipsView
        recording={recording}
        clips={[highlight]}
        client={mockClient()}
        onBack={() => {}}
        onOpen={() => {}}
        onToggleFavorite={() => {}}
        onDelete={onDelete}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Delete Team kill' }));
    expect(onDelete).toHaveBeenCalledWith(highlight);
  });
});
