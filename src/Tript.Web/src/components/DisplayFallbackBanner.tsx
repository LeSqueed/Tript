// SPDX-License-Identifier: GPL-2.0-or-later
//
// A dismissible banner for the recorder falling back off the preferred monitor. It rides the
// `settings` push (`displayFallbackWarning`, a sibling of `settings`) and is mounted in the shell
// rather than on the capture page, because the fallback is happening whether or not the user is
// looking at that page.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { DisplayFallbackWarning, SettingsMessageContent } from '../settings/settingsModel';
import {
  formatFallbackWarning,
  readDisplayFallbackWarning,
  shouldShowFallbackWarning,
} from '../settings/displayModel';

export function DisplayFallbackBanner({ client }: { client: IpcClient }) {
  const [warning, setWarning] = useState<DisplayFallbackWarning | null>(null);
  const [dismissedId, setDismissedId] = useState<string | null>(null);

  useEffect(() => {
    return client.on('settings', (content) => {
      const message = content as SettingsMessageContent | null;
      setWarning(readDisplayFallbackWarning(message?.displayFallbackWarning));
    });
  }, [client]);

  if (warning === null || !shouldShowFallbackWarning(warning, dismissedId)) {
    return null;
  }

  return (
    <div className="error-banner warning" role="status" data-testid="display-fallback-banner">
      <span className="error-banner-message">{formatFallbackWarning(warning)}</span>
      <button
        type="button"
        className="error-banner-dismiss"
        onClick={() => setDismissedId(warning.requestedId)}
        aria-label="Dismiss monitor warning"
      >
        ×
      </button>
    </div>
  );
}
