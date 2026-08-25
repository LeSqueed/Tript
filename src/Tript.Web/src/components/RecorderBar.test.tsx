// SPDX-License-Identifier: GPL-2.0-or-later

import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { IpcClient } from '../ipc/websocketClient';
import { RecorderBar } from './RecorderBar';

function mockClient(): IpcClient & {
  sent: { method: string; parameters?: unknown }[];
  emit(content: unknown): void;
} {
  const handlers = new Set<(content: unknown) => void>();
  const sent: { method: string; parameters?: unknown }[] = [];
  return {
    sent,
    state: 'connected',
    connect: () => {},
    close: () => {},
    send: (method, parameters) => sent.push({ method, parameters }),
    on: (method, handler) => {
      if (method === 'state') handlers.add(handler);
      return () => handlers.delete(handler);
    },
    onStateChange: () => () => {},
    emit: (content) => handlers.forEach((handler) => handler(content)),
  };
}

afterEach(cleanup);

describe('RecorderBar', () => {
  it('shows the detected game and records that specific game', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => client.emit({
      state: {
        recording: false,
        game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
      },
    }));

    const bar = within(screen.getByTestId('recorder-bar'));
    expect(bar.getByText('Detected: Counter-Strike 2')).toBeTruthy();
    fireEvent.click(bar.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({ method: 'StartRecording', parameters: { gameId: 'cs2' } });
  });

  it('keeps parameterless recording when no game is detected', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({ method: 'StartRecording', parameters: undefined });
  });
});
