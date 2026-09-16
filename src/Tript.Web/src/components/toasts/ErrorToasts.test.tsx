// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ErrorToasts } from './ErrorToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { IpcClient } from '../../ipc/websocketClient';

function fakeClient(): {
  client: IpcClient;
  emitError: (content: unknown) => void;
} {
  const handlers = new Map<string, (content: unknown) => void>();
  const client: IpcClient = {
    state: 'connecting',
    on(method, handler) {
      handlers.set(method, handler);
      return () => {
        handlers.delete(method);
      };
    },
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  const emitError = (content: unknown) => {
    const handler = handlers.get('error');
    if (handler) {
      handler(content);
    }
  };
  return { client, emitError };
}

function renderBridge(client: IpcClient): void {
  render(
    <ToastProvider>
      <ErrorToasts client={client} />
    </ToastProvider>,
  );
}

describe('ErrorToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('renders nothing before any error push', () => {
    const { client } = fakeClient();
    renderBridge(client);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows the backend error message and clears on dismiss', () => {
    const { client, emitError } = fakeClient();
    renderBridge(client);
    act(() => {
      emitError({ message: 'The bookmark could not be saved. Check the recording folder is writable.' });
    });
    expect(screen.getByRole('alert').textContent).toContain(
      'The bookmark could not be saved. Check the recording folder is writable.',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('takes itself down on its reading time without a click', () => {
    const { client, emitError } = fakeClient();
    renderBridge(client);
    act(() => {
      emitError({ message: 'x'.repeat(80) });
    });

    act(() => vi.advanceTimersByTime(4799));
    expect(screen.getByRole('alert')).toBeTruthy();

    act(() => vi.advanceTimersByTime(1));
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ignores a malformed error push (no string message)', () => {
    const { client, emitError } = fakeClient();
    renderBridge(client);
    act(() => {
      emitError({});
      emitError({ message: 42 });
      emitError(null);
    });
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
