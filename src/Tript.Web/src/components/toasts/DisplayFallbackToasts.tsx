// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { SettingsMessageContent } from '../../settings/settingsModel';
import {
  formatFallbackWarning,
  readDisplayFallbackWarning,
  shouldShowFallbackWarning,
} from '../../settings/displayModel';
import { useToast } from '../ui/toast/ToastProvider';

export function DisplayFallbackToasts({ client }: { client: IpcClient }) {
  const { push, dismiss } = useToast();
  const [dismissedId, setDismissedId] = useState<string | null>(null);

  useEffect(() => {
    return client.on('settings', (content) => {
      const message = content as SettingsMessageContent | null;
      const warning = readDisplayFallbackWarning(message?.displayFallbackWarning);
      if (warning !== null && shouldShowFallbackWarning(warning, dismissedId)) {
        push({
          key: 'display-fallback',
          kind: 'warning',
          message: formatFallbackWarning(warning),
          duration: 0,
          onDismiss: () => setDismissedId(warning.requestedId),
          testId: 'display-fallback-banner',
        });
      } else {
        dismiss('display-fallback');
      }
    });
  }, [client, push, dismiss, dismissedId]);

  return null;
}
