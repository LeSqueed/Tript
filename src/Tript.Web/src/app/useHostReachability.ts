// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import { probeHost, type HostReachability } from '../ipc/hostProbe';
import type { ConnectionState } from '../ipc/websocketClient';

const DEFAULT_GRACE_MS = 4_000;
const DEFAULT_POLL_MS = 15_000;

export interface HostReachabilityOptions {
  graceMs?: number;
  pollMs?: number;
  probe?: () => Promise<HostReachability>;
}

export function useHostReachability(
  connectionState: ConnectionState,
  options: HostReachabilityOptions = {},
): HostReachability | null {
  const { graceMs = DEFAULT_GRACE_MS, pollMs = DEFAULT_POLL_MS, probe = probeHost } = options;
  const [reachability, setReachability] = useState<HostReachability | null>(null);

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
