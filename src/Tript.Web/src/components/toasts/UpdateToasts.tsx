// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import type { UpdateProgressMessage } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { useToast } from '../ui/toast/ToastProvider';

export function UpdateToasts({ client }: { client: IpcClient }) {
  const { push, dismiss } = useToast();

  useEffect(() => client.on('updateProgress', (content) => {
    const status = content as UpdateProgressMessage | null;
    if (!status || status.stage === 'idle' || status.stage === 'checking' || status.stage === 'downloading') {
      return;
    }

    const releaseUrl = status.releaseUrl;
    const viewOnGitHub = releaseUrl
      ? [{ label: 'View on GitHub', onClick: () => client.send('OpenInBrowser', { url: releaseUrl }) }]
      : [];

    if (status.stage === 'ready') {
      push({
        key: 'update',
        kind: 'info',
        duration: 0,
        message: `Tript ${status.version ?? ''} is ready to install.`.replace('  ', ' '),
        actions: [{ label: 'Restart & update', onClick: () => client.send('ApplyUpdate') }, ...viewOnGitHub],
      });
    } else if (status.stage === 'available') {
      push({
        key: 'update',
        kind: 'info',
        duration: 0,
        message: `Tript ${status.version ?? ''} is available.`.replace('  ', ' '),
        actions: viewOnGitHub,
      });
    } else {
      dismiss('update');
    }
  }), [client, push, dismiss]);

  return null;
}
