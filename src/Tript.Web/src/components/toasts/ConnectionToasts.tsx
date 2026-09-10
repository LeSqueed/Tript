// SPDX-License-Identifier: GPL-2.0-or-later

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
