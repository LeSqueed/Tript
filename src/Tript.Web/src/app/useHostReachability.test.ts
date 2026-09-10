// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { useHostReachability } from './useHostReachability';
import type { HostReachability } from '../ipc/hostProbe';

const GRACE_MS = 1_000;
const POLL_MS = 5_000;

async function advance(ms: number): Promise<void> {
  await act(async () => {
    vi.advanceTimersByTime(ms);
  });
}

describe('useHostReachability', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('holds off until the socket has been down for the grace period', async () => {
    const probe = vi.fn<() => Promise<HostReachability>>(async () => 'rejected');
    const { result } = renderHook(() =>
      useHostReachability('disconnected', { graceMs: GRACE_MS, pollMs: POLL_MS, probe }),
    );

    await advance(GRACE_MS - 1);
    expect(probe).not.toHaveBeenCalled();
    expect(result.current).toBeNull();

    await advance(1);
    expect(probe).toHaveBeenCalledTimes(1);
    expect(result.current).toBe('rejected');
  });

  it('keeps counting across the reconnect flap between connecting and disconnected', async () => {
    const probe = vi.fn<() => Promise<HostReachability>>(async () => 'unreachable');
    const { rerender } = renderHook(
      ({ state }: { state: 'connecting' | 'disconnected' }) =>
        useHostReachability(state, { graceMs: GRACE_MS, pollMs: POLL_MS, probe }),
      { initialProps: { state: 'disconnected' } as { state: 'connecting' | 'disconnected' } },
    );

    await advance(GRACE_MS / 2);
    rerender({ state: 'connecting' });
    await advance(GRACE_MS / 2);

    expect(probe).toHaveBeenCalledTimes(1);
  });

  it('asks again while the socket stays down, so the answer can change', async () => {
    const answers: HostReachability[] = ['unreachable', 'rejected'];
    const probe = vi.fn<() => Promise<HostReachability>>(async () => answers.shift() ?? 'rejected');
    const { result } = renderHook(() =>
      useHostReachability('disconnected', { graceMs: GRACE_MS, pollMs: POLL_MS, probe }),
    );

    await advance(GRACE_MS);
    expect(result.current).toBe('unreachable');

    await advance(POLL_MS);
    expect(probe).toHaveBeenCalledTimes(2);
    expect(result.current).toBe('rejected');
  });

  it('clears the answer and stops asking once the socket connects', async () => {
    const probe = vi.fn<() => Promise<HostReachability>>(async () => 'rejected');
    const { result, rerender } = renderHook(
      ({ state }: { state: 'connected' | 'disconnected' }) =>
        useHostReachability(state, { graceMs: GRACE_MS, pollMs: POLL_MS, probe }),
      { initialProps: { state: 'disconnected' } as { state: 'connected' | 'disconnected' } },
    );

    await advance(GRACE_MS);
    expect(result.current).toBe('rejected');

    rerender({ state: 'connected' });
    expect(result.current).toBeNull();

    await advance(POLL_MS * 3);
    expect(probe).toHaveBeenCalledTimes(1);
  });
});
