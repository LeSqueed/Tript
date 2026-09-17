// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useState } from 'react';
import type { ConvertToSdrParameters } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';

export function useSdrConversion(client: IpcClient): {
  jobId: string | null;
  error: string | null;
  start: (target: Omit<ConvertToSdrParameters, 'id'>) => void;
} {
  const [jobId, setJobId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => client.on('importProgress', (content) => {
    const message = content as { id?: string; status?: string; error?: string };
    if (message.id !== jobId || (message.status !== 'done' && message.status !== 'error'))
      return;
    setJobId(null);
    setError(message.status === 'error' ? message.error ?? 'SDR conversion failed.' : null);
  }), [client, jobId]);

  const start = useCallback((target: Omit<ConvertToSdrParameters, 'id'>) => {
    const id = `sdr-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    setError(null);
    setJobId(id);
    client.send('ConvertToSdr', { id, contentType: target.contentType, filePath: target.filePath });
  }, [client]);

  return { jobId, error, start };
}
