// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { WindowVisibilityMessage } from '../ipc/protocol';

function documentVisible(): boolean {
  return typeof document === 'undefined' || document.visibilityState !== 'hidden';
}

export function useWindowVisible(client: IpcClient): boolean {
  const [hostVisible, setHostVisible] = useState(true);
  const [pageVisible, setPageVisible] = useState(documentVisible);

  useEffect(() => client.on('windowVisibility', (content) => {
    const message = content as Partial<WindowVisibilityMessage> | null;
    if (typeof message?.visible === 'boolean') {
      setHostVisible(message.visible);
    }
  }), [client]);

  useEffect(() => {
    const update = () => setPageVisible(documentVisible());
    document.addEventListener('visibilitychange', update);
    return () => document.removeEventListener('visibilitychange', update);
  }, []);

  return hostVisible && pageVisible;
}
