// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { StorageReportMessage, StorageStatusMessage } from '../../ipc/protocol';
import { useIpcMessage, useSendOnConnect } from '../../app/useConnection';
import { parseStorageReport, parseStorageStatus } from './storageModel';

export interface StorageController {
  status: StorageStatusMessage | null;
  report: StorageReportMessage | null;
  refresh: () => void;
  reclaim: () => void;
}

export function useStorage(client: IpcClient): StorageController {
  const [status, setStatus] = useState<StorageStatusMessage | null>(null);
  const [report, setReport] = useState<StorageReportMessage | null>(null);

  useSendOnConnect(client, 'GetStorageStatus');
  useSendOnConnect(client, 'GetStorageReport');

  useIpcMessage(client, 'storageStatus', (content) => {
    const next = parseStorageStatus(content);
    if (next) setStatus(next);
  });

  useIpcMessage(client, 'storageReport', (content) => {
    const next = parseStorageReport(content);
    if (next) setReport(next);
  });

  const refresh = useCallback(() => {
    client.send('GetStorageReport');
    client.send('GetStorageStatus');
  }, [client]);

  const reclaim = useCallback(() => {
    client.send('ReclaimStorage', { dryRun: false });
  }, [client]);

  return { status, report, refresh, reclaim };
}
