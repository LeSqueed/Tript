// SPDX-License-Identifier: GPL-2.0-or-later
//
// The trash binding: one `trash` subscription for the shell's lifetime. It lives in the shell
// rather than in the trash screen because the LIBRARY needs it too — the delete confirmation quotes
// `retentionHours`, and a number that only arrives once you have visited the trash would leave the
// first (and most important) confirmation guessing.

import { useCallback, useEffect, useMemo, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { TrashEntry } from '../../ipc/protocol';
import { EMPTY_TRASH_STATE, parseTrashMessage, type TrashState } from './trashModel';

export interface TrashController {
  entries: TrashEntry[];
  retentionHours: number;
  /** True once a `trash` push has been seen — before that, the empty list is "unknown", not "empty". */
  loaded: boolean;
  restore(entryIds: readonly string[]): void;
  /** Permanently remove the named entries. */
  purge(entryIds: readonly string[]): void;
  /** Permanently remove everything (no `entryIds` on the wire). */
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
      // An empty list purges NOTHING on the backend (only an ABSENT `entryIds` means the whole
      // bin — that is `emptyTrash`). So this guard is not a safety catch: it just spares a
      // round trip, and the state push the backend answers every PurgeTrash with, on a no-op.
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
