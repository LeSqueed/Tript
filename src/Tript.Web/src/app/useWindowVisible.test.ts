// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { useWindowVisible } from './useWindowVisible';

function setDocumentVisibility(state: DocumentVisibilityState): void {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

describe('useWindowVisible', () => {
  beforeEach(() => {
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
    setDocumentVisibility('visible');
  });

  function setup() {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    const hook = renderHook(() => useWindowVisible(client));
    client.connect();
    const ws = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    act(() => {
      ws.serverOpen();
    });
    return { ...hook, ws };
  }

  function pushVisibility(ws: MockWebSocket, visible: boolean): void {
    act(() => {
      ws.serverMessage(JSON.stringify({ method: 'windowVisibility', content: { visible } }));
    });
  }

  it('assumes the window is visible until told otherwise', () => {
    const { result } = setup();
    expect(result.current).toBe(true);
  });

  it('follows what the shell reports', () => {
    const { result, ws } = setup();

    pushVisibility(ws, false);
    expect(result.current).toBe(false);

    pushVisibility(ws, true);
    expect(result.current).toBe(true);
  });

  it('treats a hidden document as hidden even when the shell says visible', () => {
    const { result } = setup();

    act(() => {
      setDocumentVisibility('hidden');
    });
    expect(result.current).toBe(false);

    act(() => {
      setDocumentVisibility('visible');
    });
    expect(result.current).toBe(true);
  });

  it('ignores a malformed message', () => {
    const { result, ws } = setup();
    act(() => {
      ws.serverMessage(JSON.stringify({ method: 'windowVisibility', content: { visible: 'no' } }));
    });
    expect(result.current).toBe(true);
  });
});
