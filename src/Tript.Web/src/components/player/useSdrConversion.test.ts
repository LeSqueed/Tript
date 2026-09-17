// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient } from '../../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../../ipc/test/mockWebSocket';
import { useSdrConversion } from './useSdrConversion';

describe('useSdrConversion', () => {
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
    const hook = renderHook(() => useSdrConversion(client));
    const progress = (id: string | null, status: string, error?: string) =>
      act(() => ws.serverMessage(JSON.stringify({ method: 'importProgress', content: { id, status, error } })));
    const sentId = () => {
      const frame = ws.sent.map((raw) => JSON.parse(raw) as { method: string; parameters?: { id: string } })
        .find((candidate) => candidate.method === 'ConvertToSdr');
      return frame?.parameters?.id ?? null;
    };
    return { ...hook, progress, sentId };
  }

  it('tracks a conversion until its own job finishes', () => {
    const { result, progress, sentId } = setup();

    act(() => result.current.start({ contentType: 'clip', filePath: 'clips/a.mp4' }));
    const id = sentId();
    expect(id).not.toBeNull();
    expect(result.current.jobId).toBe(id);

    progress('someone-else', 'done');
    progress(id, 'importing');
    expect(result.current.jobId).toBe(id);

    progress(id, 'done');
    expect(result.current.jobId).toBeNull();
    expect(result.current.error).toBeNull();
  });

  it('reports a failed conversion and clears the error on the next attempt', () => {
    const { result, progress, sentId } = setup();

    act(() => result.current.start({ contentType: 'highlight', filePath: 'highlights/a.mp4' }));
    progress(sentId(), 'error');
    expect(result.current.error).toBe('SDR conversion failed.');

    act(() => result.current.start({ contentType: 'highlight', filePath: 'highlights/a.mp4' }));
    expect(result.current.error).toBeNull();
  });
});
