import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IpcClient } from '../../ipc/websocketClient';
import { ToastProvider } from '../ui/toast/ToastProvider';
import { GameRecordingToasts } from './GameRecordingToasts';

function fakeClient(): {
  client: IpcClient;
  emit: (method: string, content: unknown) => void;
  sent: { method: string; parameters?: unknown }[];
} {
  const handlers = new Map<string, (content: unknown) => void>();
  const sent: { method: string; parameters?: unknown }[] = [];
  const client: IpcClient = {
    state: 'connected',
    on(method, handler) {
      handlers.set(method, handler);
      return () => handlers.delete(method);
    },
    send(method, parameters) {
      sent.push({ method, parameters });
    },
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  return { client, emit: (method, content) => handlers.get(method)?.(content), sent };
}

const PROMPT = {
  promptId: 'prompt-1',
  gameId: '01HRESOLVEDGAME000000000000',
  name: 'Example Game',
  executablePath: 'C:\\Games\\Example\\game.exe',
};

function renderToasts(client: IpcClient) {
  return render(<ToastProvider><GameRecordingToasts client={client} /></ToastProvider>);
}

describe('GameRecordingToasts', () => {
  afterEach(cleanup);

  it('ignores malformed messages and deduplicates reconnect replays', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);
    act(() => emit('gameRecordingPrompt', { promptId: 'bad' }));
    expect(screen.queryByRole('status')).toBeNull();
    act(() => {
      emit('gameRecordingPrompt', PROMPT);
      emit('gameRecordingPrompt', PROMPT);
    });
    expect(screen.getAllByRole('status')).toHaveLength(1);
    expect(screen.getByText('Record Example Game?')).toBeTruthy();
    expect(screen.getByText(/C:\\Games\\Example\\game.exe/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Dismiss notification' })).toBeNull();
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it.each([
    ['Yes', true],
    ['No', false],
  ] as const)('sends %s as an explicit recording preference', (label, record) => {
    const { client, emit, sent } = fakeClient();
    renderToasts(client);
    act(() => emit('gameRecordingPrompt', PROMPT));
    fireEvent.click(screen.getByRole('button', { name: label }));
    expect(sent).toEqual([
      { method: 'GameRecordingConfirm', parameters: { promptId: PROMPT.promptId, record } },
    ]);
  });
});
