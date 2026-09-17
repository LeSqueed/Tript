// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ContentItem, CreateClipParameters, ImportProgressMessage } from '../ipc/protocol';
import type { ConnectionState, IpcClient } from '../ipc/websocketClient';

export interface ClipJobResult {
  title: string;
  item?: ContentItem;
  error?: string;
}

export function useClipJobs(
  client: IpcClient,
  connectionState: ConnectionState,
  onFinished: (result: ClipJobResult) => void,
): { clipJobCount: number; enqueueClip: (parameters: CreateClipParameters) => void } {
  const [clipJobCount, setClipJobCount] = useState(0);
  const queuedClipJobs = useRef<CreateClipParameters[]>([]);
  const activeClipJob = useRef<CreateClipParameters | null>(null);

  const refreshCount = useCallback(() => {
    setClipJobCount(queuedClipJobs.current.length + (activeClipJob.current === null ? 0 : 1));
  }, []);

  const startNextClip = useCallback(() => {
    if (activeClipJob.current !== null) {
      return;
    }
    const next = queuedClipJobs.current.shift();
    if (next !== undefined) {
      activeClipJob.current = next;
      client.send('CreateClip', next);
    }
  }, [client]);

  const enqueueClip = useCallback((parameters: CreateClipParameters) => {
    if (connectionState !== 'connected') {
      onFinished({ title: parameters.title, error: 'Tript is not connected.' });
      return;
    }
    queuedClipJobs.current.push(parameters);
    refreshCount();
    startNextClip();
  }, [connectionState, onFinished, startNextClip, refreshCount]);

  useEffect(() => client.on('importProgress', (content) => {
    const message = content as ImportProgressMessage;
    const active = activeClipJob.current;
    if (active === null || message.id !== active.id
      || (message.status !== 'done' && message.status !== 'error')) {
      return;
    }

    activeClipJob.current = null;
    onFinished(message.status === 'done'
      ? { title: active.title, item: message.content }
      : { title: active.title, error: message.error ?? 'Clip creation failed' });
    startNextClip();
    refreshCount();
  }), [client, onFinished, startNextClip, refreshCount]);

  useEffect(() => {
    if (connectionState === 'connected' || clipJobCount === 0) {
      return;
    }
    const interrupted = [activeClipJob.current, ...queuedClipJobs.current]
      .filter((job): job is CreateClipParameters => job !== null);
    activeClipJob.current = null;
    queuedClipJobs.current = [];
    setClipJobCount(0);
    for (const job of interrupted) {
      onFinished({ title: job.title, error: 'The connection was lost during clip creation.' });
    }
  }, [connectionState, clipJobCount, onFinished]);

  return { clipJobCount, enqueueClip };
}
