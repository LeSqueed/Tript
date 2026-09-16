// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { AudioLevelsMessage } from '../ipc/protocol';

export const AUDIO_LEVEL_RENEW_MS = 2000;

export function parseAudioLevels(content: unknown): Record<string, number> | null {
  const message = content as Partial<AudioLevelsMessage> | null;
  if (!Array.isArray(message?.levels)) return null;

  const levels: Record<string, number> = {};
  for (const level of message.levels) {
    if (typeof level?.deviceId !== 'string' || typeof level.peak !== 'number') continue;
    levels[level.deviceId] = Math.min(1, Math.max(0, Number.isFinite(level.peak) ? level.peak : 0));
  }
  return levels;
}

export function useAudioLevels(client: IpcClient, visible: boolean): Record<string, number> {
  const [levels, setLevels] = useState<Record<string, number>>({});

  useEffect(() => {
    if (!visible) {
      return;
    }
    const watch = () => client.send('WatchAudioLevels');
    watch();
    const renew = setInterval(watch, AUDIO_LEVEL_RENEW_MS);
    const unsubscribeState = client.onStateChange((state) => {
      if (state === 'connected') {
        watch();
      }
    });
    const unsubscribeLevels = client.on('audioLevels', (content) => {
      const next = parseAudioLevels(content);
      if (next) setLevels(next);
    });
    return () => {
      clearInterval(renew);
      unsubscribeState();
      unsubscribeLevels();
      setLevels({});
    };
  }, [client, visible]);

  return levels;
}
