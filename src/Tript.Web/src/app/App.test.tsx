// SPDX-License-Identifier: GPL-2.0-or-later
//
// Shell render test: the recorder bar, nav and theme render; navigation switches views; the
// connection state is shown even when the backend is not running (fails gracefully). The IPC
// client is mocked at the socket level so the real client logic runs against a test double.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, act } from '@testing-library/react';
import { App } from './App';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';

/** The active socket — under StrictMode the effect runs twice, so the app's live socket is last. */
function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

describe('App shell', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  function renderApp() {
    const { factory } = createMockSocketFactory();
    const result = render(<App ipcOptions={{ createSocket: factory }} />);
    return { ...result, factory };
  }

  it('renders the recorder bar and shows connecting before the backend answers', () => {
    renderApp();
    expect(screen.getByText('Stopped')).toBeTruthy();
    expect(screen.getByTestId('connection-state').textContent).toBe('Connecting…');
  });

  it('renders the nav and switches views', () => {
    renderApp();
    expect(screen.getByRole('button', { name: 'Library' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Player' }));
    expect(screen.getByText(/Session 1/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Settings' }));
    expect(screen.getByRole('tab', { name: 'Recording' })).toBeTruthy();
  });

  it('shows Connected when the mock backend opens the socket', () => {
    renderApp();
    const ws = activeSocket();
    expect(ws).toBeDefined();
    act(() => {
      ws.serverOpen();
    });
    expect(screen.getByTestId('connection-state').textContent).toBe('Connected');
  });

  it('recorder bar shows a state push from the backend', () => {
    renderApp();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
      ws.serverMessage(
        JSON.stringify({
          method: 'state',
          content: {
            state: { recording: true, game: { id: 'cs2', name: 'Counter-Strike 2', detected: true } },
          },
        }),
      );
    });
    expect(screen.getByText('Recording')).toBeTruthy();
    expect(screen.getByText('Counter-Strike 2')).toBeTruthy();
  });
});
