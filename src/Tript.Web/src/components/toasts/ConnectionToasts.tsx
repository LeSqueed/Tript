// SPDX-License-Identifier: GPL-2.0-or-later
//
// The host-reachability state as a permanent toast: it says what is wrong while it is wrong, and
// the backend resolving it (reachability back to null) is what takes it down. Permanent on purpose
// — a connection problem that vanishes on its own in eight seconds would lie about itself.

import { useEffect } from 'react';
import type { HostReachability } from '../../ipc/hostProbe';
import { connectionNotice } from '../connectionNotice';
import { useToast } from '../ui/toast/ToastProvider';

export function ConnectionToasts({ reachability }: { reachability: HostReachability | null }) {
  const { push, dismiss } = useToast();

  useEffect(() => {
    const notice = connectionNotice(reachability);
    if (notice === null) {
      dismiss('connection');
      return;
    }
    push({
      key: 'connection',
      kind: notice.tone === 'error' ? 'error' : 'warning',
      message: notice.message,
      duration: 0,
      testId: 'connection-banner',
    });
  }, [reachability, push, dismiss]);

  return null;
}
