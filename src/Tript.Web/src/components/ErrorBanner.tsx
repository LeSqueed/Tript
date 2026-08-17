// SPDX-License-Identifier: GPL-2.0-or-later
//
// A dismissible banner for backend error pushes. The backend sends an `error` message over the
// control socket when a user action could not be persisted (e.g. a bookmark, title or delete could
// not be saved because the recording folder is unwritable). The banner surfaces the latest message
// and keeps it until dismissed, so the user can read it before it disappears.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ErrorMessage } from '../ipc/protocol';

export function ErrorBanner({ client }: { client: IpcClient }) {
  const [message, setMessage] = useState<string | null>(null);

  useEffect(() => {
    return client.on('error', (content) => {
      const error = content as ErrorMessage | null;
      if (error && typeof error.message === 'string' && error.message.length > 0) {
        setMessage(error.message);
      }
    });
  }, [client]);

  if (message === null) {
    return null;
  }

  return (
    <div className="error-banner" role="alert">
      <span className="error-banner-message">{message}</span>
      <button
        type="button"
        className="error-banner-dismiss"
        onClick={() => setMessage(null)}
        aria-label="Dismiss error"
      >
        ×
      </button>
    </div>
  );
}
