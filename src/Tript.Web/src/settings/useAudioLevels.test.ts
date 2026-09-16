// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { AUDIO_LEVEL_RENEW_MS, parseAudioLevels, useAudioLevels } from './useAudioLevels';

describe('parseAudioLevels', () => {
  it('clamps peaks into the meter range and drops malformed entries', () => {
    expect(parseAudioLevels({
      levels: [
        { deviceId: 'mic', peak: 0.5 },
        { deviceId: 'loud', peak: 3 },
        { deviceId: 'quiet', peak: -1 },
        { deviceId: 'broken', peak: Number.NaN },
        { deviceId: 7, peak: 0.2 },
        { peak: 0.2 },
      ],
    })).toEqual({ mic: 0.5, loud: 1, quiet: 0, broken: 0 });
  });

  it('ignores a message without a level list', () => {
    expect(parseAudioLevels(null)).toBeNull();
    expect(parseAudioLevels({ levels: 'nope' })).toBeNull();
  });
});

describe('useAudioLevels', () => {
  beforeEach(() => {
    MockWebSocket.reset();
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  function setup(visible: boolean) {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    client.connect();
    const ws = MockWebSocket.instances[MockWebSocket.instances.length - 1];
    act(() => ws.serverOpen());
    const hook = renderHook(({ shown }) => useAudioLevels(client, shown), { initialProps: { shown: visible } });
    const watches = () => ws.sent.filter((frame) => frame.includes('"WatchAudioLevels"')).length;
    const push = (peak: number) => act(() => ws.serverMessage(JSON.stringify({
      method: 'audioLevels',
      content: { levels: [{ deviceId: 'mic', peak }] },
    })));
    return { ...hook, watches, push };
  }

  it('keeps watching while visible and reports the latest levels', () => {
    const { result, watches, push } = setup(true);
    expect(watches()).toBe(1);

    act(() => vi.advanceTimersByTime(AUDIO_LEVEL_RENEW_MS));
    expect(watches()).toBe(2);

    push(0.25);
    expect(result.current).toEqual({ mic: 0.25 });
  });

  it('neither watches nor listens while hidden, and forgets levels when hidden again', () => {
    const { result, rerender, watches, push } = setup(false);
    push(0.9);
    expect(watches()).toBe(0);
    expect(result.current).toEqual({});

    rerender({ shown: true });
    push(0.4);
    expect(result.current).toEqual({ mic: 0.4 });

    rerender({ shown: false });
    act(() => vi.advanceTimersByTime(AUDIO_LEVEL_RENEW_MS * 3));
    expect(watches()).toBe(1);
    expect(result.current).toEqual({});
  });
});
