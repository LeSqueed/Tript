// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import type { TrainingSample, TrainingSamplePreviewMessage } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';

const MAX_PREVIEW_RETRIES = 2;

export interface SamplePreviews {
  images: Record<string, string>;
  failed: Record<string, boolean>;
  request: (sample: TrainingSample) => void;
  requestMissing: (samples: TrainingSample[]) => void;
  retry: (sample: TrainingSample) => void;
  reset: () => void;
  nextRequestNumber: () => number;
}

export function useSamplePreviews(client: IpcClient, gameId: string): SamplePreviews {
  const [images, setImages] = useState<Record<string, string>>({});
  const [failed, setFailed] = useState<Record<string, boolean>>({});
  const requests = useRef(new Map<string, string>());
  const retries = useRef(new Map<string, number>());
  const counter = useRef(0);
  const activeGameId = useRef(gameId);
  activeGameId.current = gameId;

  useEffect(() => client.on('trainingSamplePreview', (content) => {
    const preview = content as TrainingSamplePreviewMessage;
    if (preview.gameId !== activeGameId.current) return;
    if (preview.requestId && requests.current.get(preview.sample.id) !== preview.requestId) return;
    requests.current.delete(preview.sample.id);
    retries.current.delete(preview.sample.id);
    setFailed((current) => {
      if (!current[preview.sample.id]) return current;
      const next = { ...current };
      delete next[preview.sample.id];
      return next;
    });
    setImages((current) => ({ ...current, [preview.sample.id]: preview.imageData }));
  }), [client]);

  const nextRequestNumber = useCallback(() => ++counter.current, []);

  const request = useCallback((sample: TrainingSample) => {
    const currentGameId = activeGameId.current;
    if (!currentGameId) return;
    const requestId = `${++counter.current}-${sample.id}`;
    requests.current.set(sample.id, requestId);
    client.send('GetTrainingSample', { gameId: currentGameId, sampleId: sample.id, previewOnly: true, requestId });
  }, [client]);

  const requestMissing = useCallback((samples: TrainingSample[]) => {
    samples.forEach((sample) => {
      if (!images[sample.id] && !requests.current.has(sample.id)) request(sample);
    });
  }, [images, request]);

  const retry = useCallback((sample: TrainingSample) => {
    const attempts = (retries.current.get(sample.id) ?? 0) + 1;
    retries.current.set(sample.id, attempts);
    if (attempts <= MAX_PREVIEW_RETRIES) {
      request(sample);
      return;
    }
    requests.current.delete(sample.id);
    setFailed((current) => ({ ...current, [sample.id]: true }));
  }, [request]);

  const reset = useCallback(() => {
    setImages({});
    setFailed({});
    requests.current.clear();
    retries.current.clear();
  }, []);

  return { images, failed, request, requestMissing, retry, reset, nextRequestNumber };
}
