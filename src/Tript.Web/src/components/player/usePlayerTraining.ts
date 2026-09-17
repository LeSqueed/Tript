// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useState } from 'react';
import type {
  ContentItem,
  TrainingEventDefinition,
  TrainingRegionGroup,
  TrainingSampleMessage,
} from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';

interface TrainingMessage {
  training?: {
    gameId?: string;
    events?: TrainingEventDefinition[];
    regionGroups?: TrainingRegionGroup[];
    model?: unknown;
  };
}

export interface PlayerTraining {
  events: TrainingEventDefinition[];
  regionGroups: TrainingRegionGroup[];
  modelAvailable: boolean;
  sample: TrainingSampleMessage | null;
  closeSample: () => void;
  captureFrame: (item: ContentItem, timestampSeconds: number, video: HTMLVideoElement | null) => void;
}

export function usePlayerTraining(
  client: IpcClient,
  enabled: boolean,
  gameId: string | null | undefined,
): PlayerTraining {
  const [events, setEvents] = useState<TrainingEventDefinition[]>([]);
  const [regionGroups, setRegionGroups] = useState<TrainingRegionGroup[]>([]);
  const [modelAvailable, setModelAvailable] = useState(false);
  const [sample, setSample] = useState<TrainingSampleMessage | null>(null);

  useEffect(() => {
    setSample(null);
    setModelAvailable(false);
    setRegionGroups([]);
  }, [gameId]);

  useEffect(() => {
    if (!enabled || !gameId) return;
    const removeTraining = client.on('training', (content) => {
      const message = (content as TrainingMessage).training;
      if (message?.events && (!message.gameId || message.gameId === gameId)) {
        setEvents(message.events);
        setRegionGroups(message.regionGroups ?? []);
      }
      if (message?.gameId === gameId) {
        setModelAvailable(Boolean(message.model));
      }
    });
    const removeSample = client.on('trainingSample', (content) => {
      const message = content as TrainingSampleMessage & { gameId?: string };
      if (!message.gameId || message.gameId === gameId) {
        setSample(message);
      }
    });
    client.send('ListTraining', { gameId });
    return () => {
      removeTraining();
      removeSample();
    };
  }, [client, gameId, enabled]);

  const closeSample = useCallback(() => setSample(null), []);

  const captureFrame = useCallback((item: ContentItem, timestampSeconds: number, video: HTMLVideoElement | null) => {
    const itemGameId = item.gameId ?? item.game;
    if (!enabled || !itemGameId || !video || video.videoWidth === 0 || video.videoHeight === 0) {
      return;
    }
    client.send('CaptureTrainingSample', {
      gameId: itemGameId,
      filePath: item.filePath,
      timestampSeconds,
      imageWidth: video.videoWidth,
      imageHeight: video.videoHeight,
      labels: [],
    });
  }, [client, enabled]);

  return { events, regionGroups, modelAvailable, sample, closeSample, captureFrame };
}
