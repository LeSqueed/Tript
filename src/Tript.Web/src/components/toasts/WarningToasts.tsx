// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { WarningMessage } from '../../ipc/protocol';
import { useToast } from '../ui/toast/ToastProvider';

export function WarningToasts({ client }: { client: IpcClient }) {
  const { push, dismiss } = useToast();

  useEffect(() => {
    return client.on('warning', (content) => {
      const warning = content as WarningMessage | null;
      if (warning && typeof warning.message === 'string' && warning.message.length > 0) {
        push({ key: 'warning', kind: 'warning', message: warning.message });
      } else {
        dismiss('warning');
      }
    });
  }, [client, push, dismiss]);

  return null;
}
