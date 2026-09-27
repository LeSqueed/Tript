// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { useHostPreferences } from './useHostPreferences';

describe('useHostPreferences', () => {
  beforeEach(() => {
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
  });

  function setup() {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    client.connect();
    const ws = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    act(() => ws.serverOpen());
    const hook = renderHook(() => useHostPreferences(client, 'connected'));
    const push = (method: string, content: unknown) =>
      act(() => ws.serverMessage(JSON.stringify({ method, content })));
    return { ...hook, ws, push };
  }

  it('asks for the game list once connected', () => {
    const { ws } = setup();
    expect(ws.sent.some((frame) => frame.includes('"ListGames"'))).toBe(true);
  });

  it('follows the clip and delete settings', () => {
    const { result, push } = setup();
    expect(result.current.convertHdrClipsToSdr).toBe(false);

    push('settings', {
      settings: {
        general: { convertHdrClipsToSdr: true },
        recording: { deleteLinkedHighlightsByDefault: true },
      },
    });

    expect(result.current.convertHdrClipsToSdr).toBe(true);
    expect(result.current.deleteLinkedHighlightsByDefault).toBe(true);
  });

  it('tracks whether the host is recording', () => {
    const { result, push } = setup();

    push('state', { state: { recording: true } });
    expect(result.current.recording).toBe(true);

    push('state', {});
    expect(result.current.recording).toBe(true);

    push('state', { state: { recording: false } });
    expect(result.current.recording).toBe(false);
  });
});
