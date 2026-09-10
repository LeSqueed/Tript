// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useMemo, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { TrashEntry } from '../../ipc/protocol';
import { EMPTY_TRASH_STATE, parseTrashMessage, type TrashState } from './trashModel';

export interface TrashController {
  entries: TrashEntry[];
  retentionHours: number;
  loaded: boolean;
  restore(entryIds: readonly string[]): void;
  purge(entryIds: readonly string[]): void;
  emptyTrash(): void;
}

export function useTrash(client: IpcClient): TrashController {
  const [state, setState] = useState<TrashState>(EMPTY_TRASH_STATE);
  const [loaded, setLoaded] = useState(false);

  useEffect(() => {
    const unsubscribeTrash = client.on('trash', (content) => {
      const next = parseTrashMessage(content);
      if (next !== null) {
        setState(next);
        setLoaded(true);
      }
    });
    client.send('ListTrash');
    const unsubscribeState = client.onStateChange((connection) => {
      setLoaded(false);
      if (connection === 'connected') {
        client.send('ListTrash');
      }
    });
    return () => {
      unsubscribeTrash();
      unsubscribeState();
    };
  }, [client]);

  const restore = useCallback(
    (entryIds: readonly string[]) => {
      if (entryIds.length > 0) {
        client.send('RestoreTrash', { entryIds: [...entryIds] });
      }
    },
    [client],
  );

  const purge = useCallback(
    (entryIds: readonly string[]) => {
      if (entryIds.length > 0) {
        client.send('PurgeTrash', { entryIds: [...entryIds] });
      }
    },
    [client],
  );

  const emptyTrash = useCallback(() => client.send('PurgeTrash'), [client]);

  return useMemo(
    () => ({
      entries: state.entries,
      retentionHours: state.retentionHours,
      loaded,
      restore,
      purge,
      emptyTrash,
    }),
    [state, loaded, restore, purge, emptyTrash],
  );
}
