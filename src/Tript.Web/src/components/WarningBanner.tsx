// SPDX-License-Identifier: GPL-2.0-or-later
//
// A transient warning banner for recoverable backend conditions, such as a game window that has not
// appeared yet. An empty or null warning push clears it.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { WarningMessage } from '../ipc/protocol';

export function WarningBanner({ client }: { client: IpcClient }) {
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    return client.on('warning', (content) => {
      const warning = content as WarningMessage | null;
      setMessage(warning && typeof warning.message === 'string' && warning.message.length > 0
        ? warning.message
        : null);
    });
  }, [client]);

  if (message === null) {
    return null;
  }

  return (
    <div className="error-banner warning" role="status">
      <span className="error-banner-message">{message}</span>
    </div>
  );
}
