// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useSyncExternalStore, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { ContentItem } from '../../ipc/protocol';
import type { SessionSource } from './sessionSource';
import { createIpcSessionSource, type IpcSessionSource } from './ipcSessionSource';

export interface SessionList {
  sessions: ContentItem[];
  clips: ContentItem[];
  items: ContentItem[];
  loaded: boolean;
}

export function useIpcSessionSource(client: IpcClient, enabled = true): IpcSessionSource | null {
  const [source, setSource] = useState<IpcSessionSource | null>(null);

  useEffect(() => {
    if (!enabled) {
      return;
    }
    const created = createIpcSessionSource(client);
    setSource(created);
    return () => created.dispose();
  }, [client, enabled]);

  return enabled ? source : null;
}

export function useSessionSource(source: SessionSource | null): SessionList {
  const subscribe = useMemo(() => {
    const observe = source?.observeSessions;
    return observe ? (onChange: () => void) => observe(onChange) : () => () => {};
  }, [source]);

  const getSnapshot = useMemo(() => {
    const getVersion = source?.getVersion;
    return getVersion ? () => getVersion() : () => 0;
  }, [source]);

  const version = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  const sessions = useMemo(() => (source ? source.getSessions() : []), [source, version]);
  const clips = useMemo(() => (source?.getClips ? source.getClips() : []), [source, version]);
  const items = useMemo(
    () => (source?.getItems ? source.getItems() : [...sessions, ...clips]),
    [source, version, sessions, clips],
  );

  return { sessions, clips, items, loaded: source ? (source.getVersion?.() ?? 0) > 0 : false };
}
