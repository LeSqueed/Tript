// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { ErrorMessage } from '../../ipc/protocol';
import { useToast } from '../ui/toast/ToastProvider';

export function ErrorToasts({ client }: { client: IpcClient }) {
  const { push } = useToast();

  useEffect(() => {
    return client.on('error', (content) => {
      const error = content as ErrorMessage | null;
      if (error && typeof error.message === 'string' && error.message.length > 0) {
        push({ kind: 'error', message: error.message });
      }
    });
  }, [client, push]);

  return null;
}
