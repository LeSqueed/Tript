// SPDX-License-Identifier: GPL-2.0-or-later
//
// WarningToasts tests exercise the user-visible result of warning IPC pushes: one warning at a
// time (a new push replaces the old in place), an empty or null push takes it down.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { WarningToasts } from './WarningToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { IpcClient } from '../../ipc/websocketClient';

function fakeClient(): { client: IpcClient; emitWarning: (content: unknown) => void } {
  const handlers = new Map<string, (content: unknown) => void>();
  const client: IpcClient = {
    state: 'connecting',
    on(method, handler) {
      handlers.set(method, handler);
      return () => handlers.delete(method);
    },
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  return {
    client,
    emitWarning: (content) => handlers.get('warning')?.(content),
  };
}

describe('WarningToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('shows a warning pushed by the backend', () => {
    const { client, emitWarning } = fakeClient();
    render(
      <ToastProvider>
        <WarningToasts client={client} />
      </ToastProvider>,
    );

    act(() => emitWarning({ message: 'The game window has not appeared yet.' }));

    expect(screen.getByRole('status').textContent).toContain('The game window has not appeared yet.');
  });

  it('replaces the live warning when the backend pushes a new one', () => {
    const { client, emitWarning } = fakeClient();
    render(
      <ToastProvider>
        <WarningToasts client={client} />
      </ToastProvider>,
    );
    act(() => emitWarning({ message: 'Waiting for the game window.' }));
    act(() => emitWarning({ message: 'Still waiting, but the recorder is warm now.' }));

    expect(screen.queryByText('Waiting for the game window.')).toBeNull();
    expect(screen.getByText('Still waiting, but the recorder is warm now.')).toBeTruthy();
    expect(screen.getAllByRole('status')).toHaveLength(1);
  });

  it('clears the visible warning when the backend sends an empty warning', () => {
    const { client, emitWarning } = fakeClient();
    render(
      <ToastProvider>
        <WarningToasts client={client} />
      </ToastProvider>,
    );
    act(() => emitWarning({ message: 'Waiting for the game window.' }));

    act(() => emitWarning(null));
    act(() => vi.advanceTimersByTime(200));

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('does not show malformed warning content', () => {
    const { client, emitWarning } = fakeClient();
    render(
      <ToastProvider>
        <WarningToasts client={client} />
      </ToastProvider>,
    );

    act(() => {
      emitWarning({});
      emitWarning({ message: 42 });
      emitWarning({ message: '' });
    });

    expect(screen.queryByRole('status')).toBeNull();
  });
});
