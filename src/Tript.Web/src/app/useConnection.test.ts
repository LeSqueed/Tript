// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { useIpcMessage, useSendOnConnect } from './useConnection';

function sentMethods(ws: MockWebSocket): string[] {
  return ws.sent.map((frame) => (JSON.parse(frame) as { method: string }).method);
}

describe('connection hooks', () => {
  beforeEach(() => {
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
  });

  function connectedClient() {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 1 });
    client.connect();
    const ws = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    act(() => ws.serverOpen());
    return { client, ws };
  }

  it('sends once when mounted on a live connection', () => {
    const { client, ws } = connectedClient();

    renderHook(() => useSendOnConnect(client, 'ListSettings'));

    expect(sentMethods(ws).filter((method) => method === 'ListSettings')).toHaveLength(1);
  });

  it('waits for the connection when mounted before it opens, then sends on every reconnect', () => {
    vi.useFakeTimers();
    try {
      const { factory } = createMockSocketFactory();
      const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 1 });
      renderHook(() => useSendOnConnect(client, 'ListGames'));
      client.connect();
      const first = MockWebSocket.instances[MockWebSocket.instances.length - 1];
      expect(sentMethods(first)).not.toContain('ListGames');

      act(() => first.serverOpen());
      expect(sentMethods(first).filter((method) => method === 'ListGames')).toHaveLength(1);

      act(() => first.serverClose());
      act(() => vi.advanceTimersByTime(10));
      const second = MockWebSocket.instances[MockWebSocket.instances.length - 1];
      expect(second).not.toBe(first);
      act(() => second.serverOpen());
      expect(sentMethods(second).filter((method) => method === 'ListGames')).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it('delivers messages to the latest handler without resubscribing', () => {
    const { client, ws } = connectedClient();
    const seen: string[] = [];
    const subscribe = vi.spyOn(client, 'on');

    const { rerender } = renderHook(({ label }) => useIpcMessage(client, 'warning', () => seen.push(label)), {
      initialProps: { label: 'first' },
    });
    rerender({ label: 'second' });
    act(() => ws.serverMessage(JSON.stringify({ method: 'warning', content: { message: 'x' } })));

    expect(seen).toEqual(['second']);
    expect(subscribe).toHaveBeenCalledTimes(1);
  });

  it('stops listening when unmounted', () => {
    const { client, ws } = connectedClient();
    const handler = vi.fn();

    const { unmount } = renderHook(() => useIpcMessage(client, 'warning', handler));
    unmount();
    act(() => ws.serverMessage(JSON.stringify({ method: 'warning', content: { message: 'x' } })));

    expect(handler).not.toHaveBeenCalled();
  });
});
