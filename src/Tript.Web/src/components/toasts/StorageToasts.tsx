// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import { useToast } from '../ui/toast/ToastProvider';
import { formatStorageSize, parseStorageStatus } from '../storage/storageModel';

const POLICY_KEY = 'storage-policy';
const WARNING_KEY = 'storage-warning';
const CRITICAL_KEY = 'storage-critical';

export function StorageToasts({
  client,
  onOpenStorageSettings,
}: {
  client: IpcClient;
  onOpenStorageSettings: () => void;
}) {
  const { push, dismiss } = useToast();

  useEffect(() => {
    return client.on('storageStatus', (content) => {
      const status = parseStorageStatus(content);
      if (!status) {
        return;
      }

      const free = formatStorageSize(status.freeBytes);
      const where = status.volumeRoot ?? status.root;

      if (status.pressure === 'critical') {
        dismiss(POLICY_KEY);
        dismiss(WARNING_KEY);
        push({
          key: CRITICAL_KEY,
          kind: 'error',
          duration: 0,
          title: status.recordingBlocked ? 'Recording is on hold' : 'The recording drive is full',
          message: `Only ${free} is left on ${where}. Free up space to start recording again.`,
          testId: 'storage-critical-banner',
          actions: [{ label: 'Manage storage', onClick: onOpenStorageSettings }],
        });
        return;
      }

      dismiss(CRITICAL_KEY);

      if (status.pressure !== 'warning') {
        dismiss(POLICY_KEY);
        dismiss(WARNING_KEY);
        return;
      }

      if (!status.policyConfirmed) {
        dismiss(WARNING_KEY);
        push({
          key: POLICY_KEY,
          kind: 'warning',
          duration: 0,
          title: 'Decide what Tript should do when space runs out',
          message:
            `${where} is down to ${free}. Choose whether Tript pauses recording or removes the `
            + 'oldest items, before it matters. Favourites are never removed.',
          testId: 'storage-policy-banner',
          actions: [{ label: 'Choose now', onClick: onOpenStorageSettings }],
        });
        return;
      }

      dismiss(POLICY_KEY);
      push({
        key: WARNING_KEY,
        kind: 'warning',
        duration: 0,
        title: 'Space is running low',
        message:
          `${where} is down to ${free}. `
          + (status.whenFull === 'ReclaimOldest'
            ? 'Tript will start removing the oldest items when it gets tight.'
            : 'Tript will pause recording when it gets tight.'),
        testId: 'storage-warning-banner',
        actions: [{ label: 'Manage storage', onClick: onOpenStorageSettings }],
      });
    });
  }, [client, push, dismiss, onOpenStorageSettings]);

  return null;
}
