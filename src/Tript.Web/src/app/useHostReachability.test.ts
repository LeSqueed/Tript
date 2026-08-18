// SPDX-License-Identifier: GPL-2.0-or-later
//
// The grace period and the repeat are the whole behaviour: the client passes through 'disconnected'
// on every reconnect, so probing at once would put a banner on a socket that is merely retrying, and
// asking only once would leave "Tript is not running" on screen after Tript came back — which is
// exactly when the answer changes to the one worth showing.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { useHostReachability } from './useHostReachability';
import type { HostReachability } from '../ipc/hostProbe';

const GRACE_MS = 1_000;
const POLL_MS = 5_000;

/** Advances timers and lets the probe's promise settle. */
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

  // The client cycles 'connecting' → 'disconnected' → 'connecting' on every retry. Restarting the
  // grace period on each of those would mean it never elapses and the banner never appears.
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

  // A host that was down and comes back up answers 'rejected' from then on: same launch, new key.
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
