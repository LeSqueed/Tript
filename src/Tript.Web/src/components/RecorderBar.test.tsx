// SPDX-License-Identifier: GPL-2.0-or-later

import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { ModelStatusMessage } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';
import { RecorderBar } from './RecorderBar';

function mockClient(): IpcClient & {
  sent: { method: string; parameters?: unknown }[];
  emit(method: string, content: unknown): void;
} {
  const handlers = new Map<string, Set<(content: unknown) => void>>();
  const sent: { method: string; parameters?: unknown }[] = [];
  return {
    sent,
    state: 'connected',
    connect: () => {},
    close: () => {},
    send: (method, parameters) => sent.push({ method, parameters }),
    on: (method, handler) => {
      const methodHandlers = handlers.get(method) ?? new Set();
      methodHandlers.add(handler);
      handlers.set(method, methodHandlers);
      return () => methodHandlers.delete(handler);
    },
    onStateChange: () => () => {},
    emit: (method, content) => handlers.get(method)?.forEach((handler) => handler(content)),
  };
}

afterEach(cleanup);

describe('RecorderBar', () => {
  it('shows the detected game and records that specific game', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => client.emit('state', {
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

  it('shows determinate download progress for the represented game only', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    const snapshot = {
      models: [
        { gameId: 'other', stage: 'error', message: 'Wrong game' },
        { gameId: 'cs2', stage: 'downloading', completedBytes: 25, totalBytes: 100 },
      ],
    } satisfies ModelStatusMessage;
    act(() => {
      client.emit('modelStatus', snapshot);
      client.emit('state', {
        state: {
          recording: false,
          game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
        },
      });
    });

    const progress = screen.getByRole('progressbar', { name: 'Downloading game model: 25%' });
    expect(progress.getAttribute('aria-valuenow')).toBe('25');
    expect(progress.textContent).toContain('Model 25%');
    expect(screen.queryByText(/Wrong game/)).toBeNull();
  });

  it('uses indeterminate progress semantics while verifying', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => {
      client.emit('state', {
        state: { recording: true, game: { id: 'cs2', name: 'CS2', detected: true } },
      });
      client.emit('modelStatus', {
        models: [{ gameId: 'cs2', stage: 'verifying' }],
      } satisfies ModelStatusMessage);
    });

    const progress = screen.getByRole('progressbar', { name: 'Verifying model' });
    expect(progress.getAttribute('aria-valuenow')).toBeNull();
    expect(progress.textContent).toContain('Verifying model');
  });

  it('does not retain ready or statuses omitted from a replacement snapshot', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => {
      client.emit('state', {
        state: { recording: false, game: { id: 'cs2', name: 'CS2', detected: true } },
      });
      client.emit('modelStatus', {
        models: [{ gameId: 'cs2', stage: 'unsupported', message: 'No model available' }],
      } satisfies ModelStatusMessage);
    });
    expect(screen.getByRole('status').textContent).toBe('Model unsupported: No model available');

    act(() => client.emit('modelStatus', {
      models: [{ gameId: 'cs2', stage: 'ready', revision: 2 }],
    } satisfies ModelStatusMessage));
    expect(screen.queryByTestId('model-status')).toBeNull();

    act(() => client.emit('modelStatus', { models: [] } satisfies ModelStatusMessage));
    expect(screen.queryByTestId('model-status')).toBeNull();
  });
});
