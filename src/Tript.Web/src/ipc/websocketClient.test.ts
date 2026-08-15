// SPDX-License-Identifier: GPL-2.0-or-later
//
// IPC client tests: NewConnection on socket open, dispatch of incoming messages, reconnection
// with backoff, and graceful failure when the backend is not running.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createIpcClient } from './websocketClient';
import { MockWebSocket, createMockSocketFactory } from './test/mockWebSocket';

describe('createIpcClient', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    MockWebSocket.reset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('sends NewConnection with the protocol version immediately on socket open', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 10 });
    client.connect();

    const ws = MockWebSocket.instances[0];
    expect(ws).toBeDefined();
    ws.serverOpen();

    expect(ws.sent).toHaveLength(1);
    expect(JSON.parse(ws.sent[0])).toEqual({ method: 'NewConnection', parameters: { protocolVersion: 1 } });
  });

  it('dispatches incoming backend frames by method', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    const handler = vi.fn();
    client.on('state', handler);
    client.connect();

    MockWebSocket.instances[0].serverOpen();
    MockWebSocket.instances[0].serverMessage(
      '{"method":"state","content":{"state":{"recording":true},"cause":"startRecording"}}',
    );

    expect(handler).toHaveBeenCalledWith({
      state: { recording: true },
      cause: 'startRecording',
    });
  });

  it('reconnects after a server-side close, with backoff', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 100, reconnectMaxDelayMs: 400 });
    const states: string[] = [];
    client.onStateChange((s) => states.push(s));
    client.connect();

    MockWebSocket.instances[0].serverOpen();
    MockWebSocket.instances[0].serverClose();

    expect(client.state).toBe('disconnected');
    expect(MockWebSocket.instances).toHaveLength(1);

    // A new connection is attempted after 100ms. It opens and then closes, scheduling the next.
    vi.advanceTimersByTime(100);
    expect(MockWebSocket.instances).toHaveLength(2);
    MockWebSocket.instances[1].serverOpen();
    MockWebSocket.instances[1].serverClose();

    // Second retry after 200ms (doubled).
    vi.advanceTimersByTime(200);
    expect(MockWebSocket.instances).toHaveLength(3);
    MockWebSocket.instances[2].serverOpen();
    MockWebSocket.instances[2].serverClose();

    // Third retry after 400ms (doubled again, capped at max).
    vi.advanceTimersByTime(400);
    expect(MockWebSocket.instances).toHaveLength(4);

    expect(states).toContain('connecting');
    expect(states).toContain('connected');
    expect(states).toContain('disconnected');
  });

  it('reconnects when the constructor throws (backend not running)', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 50, reconnectMaxDelayMs: 200 });
    MockWebSocket.throwOnNextConstruct = true;
    client.connect();

    // The first construction failed; a retry is scheduled.
    expect(MockWebSocket.instances).toHaveLength(0);
    vi.advanceTimersByTime(50);
    expect(MockWebSocket.instances).toHaveLength(1);
  });

  it('connect() after close() does not resurrect a closed socket, and close() stops reconnecting', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 50 });
    client.connect();
    MockWebSocket.instances[0].serverOpen();

    client.close();
    expect(client.state).toBe('disconnected');

    // No further connections should be attempted.
    vi.advanceTimersByTime(1000);
    expect(MockWebSocket.instances).toHaveLength(1);
  });

  it('send() before open is dropped, not queued', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    client.connect();

    client.send('StartRecording');
    const ws = MockWebSocket.instances[0];
    // Only NewConnection is sent once open; the pre-open StartRecording is dropped.
    ws.serverOpen();
    expect(ws.sent).toHaveLength(1);
    expect(JSON.parse(ws.sent[0]).method).toBe('NewConnection');
  });

  it('tracks state transitions', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    const states: string[] = [];
    client.onStateChange((s) => states.push(s));

    expect(client.state).toBe('disconnected');
    client.connect();
    expect(client.state).toBe('connecting');
    MockWebSocket.instances[0].serverOpen();
    expect(client.state).toBe('connected');
    MockWebSocket.instances[0].serverClose();
    expect(client.state).toBe('disconnected');

    expect(states).toEqual(['connecting', 'connected', 'disconnected']);
  });

  it('ignores frames from a stale socket that was replaced', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory, reconnectBaseDelayMs: 50 });
    const handler = vi.fn();
    client.on('state', handler);
    client.connect();

    const first = MockWebSocket.instances[0];
    first.serverOpen();
    first.serverClose();
    vi.advanceTimersByTime(50);

    const second = MockWebSocket.instances[1];
    expect(second).toBeDefined();
    second.serverOpen();
    second.serverMessage('{"method":"state","content":{"recording":true}}');
    expect(handler).toHaveBeenCalledTimes(1);

    // A late frame from the first socket must be ignored.
    first.serverMessage('{"method":"state","content":{"recording":false}}');
    expect(handler).toHaveBeenCalledTimes(1);
  });
});
