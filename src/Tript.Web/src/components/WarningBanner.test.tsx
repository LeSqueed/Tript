// SPDX-License-Identifier: GPL-2.0-or-later
//
// WarningBanner tests exercise the user-visible result of warning IPC pushes. The client double
// captures the registered handler so this stays independent of socket timing and reconnects.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { WarningBanner } from './WarningBanner';
import type { IpcClient } from '../ipc/websocketClient';

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

describe('WarningBanner', () => {
  afterEach(() => cleanup());

  it('shows a warning pushed by the backend', () => {
    const { client, emitWarning } = fakeClient();
    render(<WarningBanner client={client} />);

    act(() => emitWarning({ message: 'The game window has not appeared yet.' }));

    expect(screen.getByRole('status').textContent).toContain('The game window has not appeared yet.');
  });

  it('clears the visible warning when the backend sends an empty warning', () => {
    const { client, emitWarning } = fakeClient();
    render(<WarningBanner client={client} />);
    act(() => emitWarning({ message: 'Waiting for the game window.' }));

    act(() => emitWarning(null));

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('does not show malformed warning content', () => {
    const { client, emitWarning } = fakeClient();
    render(<WarningBanner client={client} />);

    act(() => {
      emitWarning({});
      emitWarning({ message: 42 });
      emitWarning({ message: '' });
    });

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('has no dismiss control because warnings are cleared by the backend', () => {
    const { client, emitWarning } = fakeClient();
    render(<WarningBanner client={client} />);
    act(() => emitWarning({ message: 'Waiting for the game window.' }));

    expect(screen.queryByRole('button')).toBeNull();
  });
});
