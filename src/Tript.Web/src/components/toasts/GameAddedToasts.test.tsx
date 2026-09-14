import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IpcClient } from '../../ipc/websocketClient';
import { ToastProvider } from '../ui/toast/ToastProvider';
import { GameAddedToasts } from './GameAddedToasts';

function fakeClient(): {
  client: IpcClient;
  emit: (method: string, content: unknown) => void;
} {
  const handlers = new Map<string, (content: unknown) => void>();
  const client: IpcClient = {
    state: 'connected',
    on(method, handler) {
      handlers.set(method, handler);
      return () => handlers.delete(method);
    },
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  return { client, emit: (method, content) => handlers.get(method)?.(content) };
}

const ADDED = {
  gameId: '01HRESOLVEDGAME000000000000',
  name: 'Example Game',
  executablePath: 'C:\\Games\\Example\\game.exe',
};

function renderToasts(client: IpcClient, onOpenGameSettings = vi.fn()) {
  render(<ToastProvider><GameAddedToasts client={client} onOpenGameSettings={onOpenGameSettings} /></ToastProvider>);
  return onOpenGameSettings;
}

describe('GameAddedToasts', () => {
  afterEach(cleanup);

  it('ignores malformed messages', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);
    act(() => emit('gameAdded', { gameId: 'only-a-game-id' }));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('shows a dismissible notice naming the added game', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);
    act(() => emit('gameAdded', ADDED));

    expect(screen.getByText('Example Game was added to your games.')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Dismiss notification' })).toBeTruthy();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('opens that game\'s settings when the action is clicked', () => {
    const { client, emit } = fakeClient();
    const onOpenGameSettings = renderToasts(client);
    act(() => emit('gameAdded', ADDED));

    fireEvent.click(screen.getByRole('button', { name: 'Go to settings' }));

    expect(onOpenGameSettings).toHaveBeenCalledWith(ADDED.gameId);
  });
});
