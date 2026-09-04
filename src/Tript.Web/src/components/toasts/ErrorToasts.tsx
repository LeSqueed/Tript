// SPDX-License-Identifier: GPL-2.0-or-later
//
// Backend error pushes as timed toasts: the message gets its reading time, then slides out on its
// own. An error means a user action could not be persisted (a bookmark, title or delete the host
// refused), so the toast says what failed and how long it stays is derived from the message.

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
