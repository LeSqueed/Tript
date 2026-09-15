// SPDX-License-Identifier: GPL-2.0-or-later

import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { IpcClient } from '../../ipc/websocketClient';
import { ToastProvider } from '../ui/toast/ToastProvider';
import { UpdateToasts } from './UpdateToasts';

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

function renderToasts(client: IpcClient) {
  render(<ToastProvider><UpdateToasts client={client} /></ToastProvider>);
}

describe('UpdateToasts', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('shows nothing for idle, checking or downloading stages', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);

    act(() => emit('updateProgress', { stage: 'checking' }));
    act(() => emit('updateProgress', { stage: 'downloading', completedBytes: 10, totalBytes: 100 }));

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('shows a persistent notice with a restart action once an update is ready', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);

    act(() => emit('updateProgress', { stage: 'ready', version: '0.1.0-alpha.3' }));

    expect(screen.getByText('Tript 0.1.0-alpha.3 is ready to install.')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Restart & update' }));
    expect(client.send).toHaveBeenCalledWith('ApplyUpdate');
  });

  it('shows a view-on-GitHub action once a release is available, without a restart action', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);

    act(() => emit('updateProgress', {
      stage: 'available',
      version: '0.1.0-alpha.3',
      releaseUrl: 'https://github.com/LeSqueed/Tript/releases/tag/v0.1.0-alpha.3',
    }));

    expect(screen.getByText('Tript 0.1.0-alpha.3 is available.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Restart & update' })).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'View on GitHub' }));
    expect(client.send).toHaveBeenCalledWith('OpenInBrowser', {
      url: 'https://github.com/LeSqueed/Tript/releases/tag/v0.1.0-alpha.3',
    });
  });

  it('dismisses the notice once a check reports up to date', () => {
    const { client, emit } = fakeClient();
    renderToasts(client);
    act(() => emit('updateProgress', { stage: 'ready', version: '0.1.0-alpha.3' }));
    expect(screen.queryByRole('status')).not.toBeNull();

    act(() => emit('updateProgress', { stage: 'upToDate' }));
    act(() => vi.advanceTimersByTime(200));

    expect(screen.queryByRole('status')).toBeNull();
  });
});
