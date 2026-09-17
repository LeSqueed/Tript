// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient, type ConnectionState } from '../ipc/websocketClient';
import type { CreateClipParameters } from '../ipc/protocol';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { useClipJobs, type ClipJobResult } from './useClipJobs';

function clip(id: string): CreateClipParameters {
  return {
    id,
    type: 'clip',
    fileName: 'session.mp4',
    filePath: 'sessions/session.mp4',
    title: `Clip ${id}`,
    startTime: 0,
    endTime: 5,
    segments: [],
    outputMode: 'combine',
  };
}

describe('useClipJobs', () => {
  beforeEach(() => {
    MockWebSocket.reset();
  });

  afterEach(() => {
    cleanup();
  });

  function setup(initialState: ConnectionState = 'connected') {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    client.connect();
    const ws = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    act(() => ws.serverOpen());
    const finished = vi.fn<(result: ClipJobResult) => void>();
    const hook = renderHook(
      ({ state }) => useClipJobs(client, state, finished),
      { initialProps: { state: initialState } },
    );
    const createClipIds = () => ws.sent
      .map((frame) => JSON.parse(frame) as { method: string; parameters?: { id?: string } })
      .filter((frame) => frame.method === 'CreateClip')
      .map((frame) => frame.parameters?.id);
    const progress = (id: string, status: 'done' | 'error', extra: object = {}) =>
      act(() => ws.serverMessage(JSON.stringify({ method: 'importProgress', content: { id, status, ...extra } })));
    return { ...hook, finished, createClipIds, progress };
  }

  it('sends one clip at a time and starts the next when the first finishes', () => {
    const { result, finished, createClipIds, progress } = setup();

    act(() => {
      result.current.enqueueClip(clip('a'));
      result.current.enqueueClip(clip('b'));
    });
    expect(createClipIds()).toEqual(['a']);
    expect(result.current.clipJobCount).toBe(2);

    progress('b', 'done');
    expect(createClipIds()).toEqual(['a']);

    progress('a', 'done', { content: { filePath: 'clips/a.mp4' } });
    expect(createClipIds()).toEqual(['a', 'b']);
    expect(result.current.clipJobCount).toBe(1);
    expect(finished).toHaveBeenCalledWith({ title: 'Clip a', item: { filePath: 'clips/a.mp4' } });

    progress('b', 'error', { error: 'disk full' });
    expect(result.current.clipJobCount).toBe(0);
    expect(finished).toHaveBeenLastCalledWith({ title: 'Clip b', error: 'disk full' });
  });

  it('refuses a clip while disconnected', () => {
    const { result, finished, createClipIds } = setup('disconnected');

    act(() => result.current.enqueueClip(clip('a')));

    expect(createClipIds()).toEqual([]);
    expect(finished).toHaveBeenCalledWith({ title: 'Clip a', error: 'Tript is not connected.' });
  });

  it('fails every pending clip when the connection drops', () => {
    const { result, rerender, finished } = setup();
    act(() => {
      result.current.enqueueClip(clip('a'));
      result.current.enqueueClip(clip('b'));
    });

    rerender({ state: 'disconnected' });

    expect(result.current.clipJobCount).toBe(0);
    expect(finished.mock.calls.map(([call]) => call)).toEqual([
      { title: 'Clip a', error: 'The connection was lost during clip creation.' },
      { title: 'Clip b', error: 'The connection was lost during clip creation.' },
    ]);
  });
});
