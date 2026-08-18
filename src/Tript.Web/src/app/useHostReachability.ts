// SPDX-License-Identifier: GPL-2.0-or-later
//
// Why the control socket is down, when it has been down long enough to be worth saying.
//
// The socket itself cannot tell a rejected key from a dead host (see ipc/hostProbe.ts), so this asks
// the UI host directly. It waits out a grace period first: every reconnect passes through
// 'disconnected', and a banner that flickers on each retry teaches the user to ignore it. It keeps
// asking afterwards, because the answer changes — a host that was 'unreachable' and comes back up
// answers 'rejected' from then on, which is the case this whole path exists for.

import { useEffect, useState } from 'react';
import { probeHost, type HostReachability } from '../ipc/hostProbe';
import type { ConnectionState } from '../ipc/websocketClient';

const DEFAULT_GRACE_MS = 4_000;
const DEFAULT_POLL_MS = 15_000;

export interface HostReachabilityOptions {
  /** How long the socket must be down before the host is asked about it. */
  graceMs?: number;
  /** How often to ask again while it stays down. */
  pollMs?: number;
  probe?: () => Promise<HostReachability>;
}

/** The host's answer while the socket is down, or null while it is up (or not down long enough). */
export function useHostReachability(
  connectionState: ConnectionState,
  options: HostReachabilityOptions = {},
): HostReachability | null {
  const { graceMs = DEFAULT_GRACE_MS, pollMs = DEFAULT_POLL_MS, probe = probeHost } = options;
  const [reachability, setReachability] = useState<HostReachability | null>(null);

  // Not on `connectionState`: the client flaps between 'connecting' and 'disconnected' on every
  // retry, and an effect keyed on that would restart the grace period before it ever elapsed.
  const connected = connectionState === 'connected';

  useEffect(() => {
    if (connected) {
      setReachability(null);
      return;
    }
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> = setTimeout(ask, graceMs);

    async function ask(): Promise<void> {
      const answer = await probe();
      if (cancelled) {
        return;
      }
      setReachability(answer);
      timer = setTimeout(ask, pollMs);
    }

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [connected, graceMs, pollMs, probe]);

  return reachability;
}
