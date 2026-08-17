// SPDX-License-Identifier: GPL-2.0-or-later
//
// The session-source React bindings.
//
// `useIpcSessionSource` owns an IPC-backed source for the lifetime of a view: it is created once
// per mount (in an effect, so StrictMode's double-invoke disposes the first and keeps the second),
// sends `ListContent` on creation, and is disposed on unmount — a view leaving the route stops
// listening. `enabled` lets a caller that provides its own `SessionSource` prop opt out entirely,
// so an injected test source never trips a stray `ListContent` send.
//
// `useSessionSource` is the reactive read. The IPC source keeps the same object across `content`
// pushes, so a `useMemo` over the source would never re-run. This hook subscribes through the
// seam's `observeSessions` and uses the source's monotonic `getVersion()` as the
// `useSyncExternalStore` snapshot: every push re-renders the consumer and the lists are re-read
// (memoised on the version). A static source (the stub or a test's injected source) has neither
// `observeSessions` nor `getVersion` — the snapshot is the constant 0, the subscription is a
// no-op, and the lists never change, exactly as before.

import { useEffect, useMemo, useSyncExternalStore, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { ContentItem } from '../../ipc/protocol';
import type { SessionSource } from './sessionSource';
import { createIpcSessionSource, type IpcSessionSource } from './ipcSessionSource';

export interface SessionList {
  /** Sessions (contentType === 'recording') in list order. */
  sessions: ContentItem[];
  /** Clips (contentType === 'clip') in list order. */
  clips: ContentItem[];
  /**
   * The whole list in the backend's order — what the library grid renders over. Falls back to
   * sessions-then-clips for a static source that has no `getItems` (see the seam's note on why the
   * concatenation is a fallback and not the definition).
   */
  items: ContentItem[];
}

/**
 * Create an IPC-backed session source for the lifetime of the calling view. Returns null until the
 * creation effect has run, so the first render sees an empty list — the same state the view is in
 * before the backend answers `ListContent`.
 */
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

/**
 * Bind to a session source and return its sessions and clips, re-rendering whenever the source
 * pushes a content change. Safe for both the IPC source (reactive) and static sources (constant).
 */
export function useSessionSource(source: SessionSource | null): SessionList {
  const subscribe = useMemo(() => {
    const observe = source?.observeSessions;
    return observe ? (onChange: () => void) => observe(onChange) : () => () => {};
  }, [source]);

  const getSnapshot = useMemo(() => {
    const getVersion = source?.getVersion;
    return getVersion ? () => getVersion() : () => 0;
  }, [source]);

  // A number snapshot is always "cached" (Object.is on primitives), so it is safe for
  // useSyncExternalStore whether or not the source is reactive.
  const version = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);

  const sessions = useMemo(() => (source ? source.getSessions() : []), [source, version]);
  const clips = useMemo(() => (source?.getClips ? source.getClips() : []), [source, version]);
  const items = useMemo(
    () => (source?.getItems ? source.getItems() : [...sessions, ...clips]),
    [source, version, sessions, clips],
  );

  return { sessions, clips, items };
}
