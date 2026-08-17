// SPDX-License-Identifier: GPL-2.0-or-later
//
// ErrorBanner tests: an `error` push on the IPC client shows the backend's message in a dismissible
// banner; the banner is absent until an error arrives and is cleared by the dismiss button. The IPC
// client is a fake that captures the registered `error` handler, so the wire shape is exercised
// directly without a socket.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, act } from '@testing-library/react';
import { ErrorBanner } from './ErrorBanner';
import type { IpcClient } from '../ipc/websocketClient';

/** A fake IpcClient that captures the `error` handler so a test can fire it directly. */
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

describe('ErrorBanner', () => {
  afterEach(() => {
    cleanup();
  });

  it('renders nothing before any error push', () => {
    const { client } = fakeClient();
    render(<ErrorBanner client={client} />);
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows the backend error message and clears on dismiss', () => {
    const { client, emitError } = fakeClient();
    render(<ErrorBanner client={client} />);
    act(() => {
      emitError({ message: 'The bookmark could not be saved — check the recording folder is writable.' });
    });
    expect(screen.getByRole('alert').textContent).toContain(
      'The bookmark could not be saved — check the recording folder is writable.',
    );

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss error' }));
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('ignores a malformed error push (no string message)', () => {
    const { client, emitError } = fakeClient();
    render(<ErrorBanner client={client} />);
    act(() => {
      emitError({});
      emitError({ message: 42 });
      emitError(null);
    });
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
