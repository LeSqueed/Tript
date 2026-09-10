// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { useTrash } from './useTrash';
import type { ConnectionState, IpcClient } from '../../ipc/websocketClient';

function recordingClient() {
  const sent: { method: string; parameters?: unknown }[] = [];
  const handlers = new Map<string, (content: unknown) => void>();
  let onState: ((state: ConnectionState) => void) | null = null;
  const client: IpcClient = {
    state: 'connected',
    connect: () => {},
    close: () => {},
    send: (method, parameters) => sent.push({ method, parameters }),
    on: (method, handler) => {
      handlers.set(method, handler);
      return () => handlers.delete(method);
    },
    onStateChange: (handler) => {
      onState = handler;
      return () => {
        onState = null;
      };
    },
  };
  return {
    client,
    sent,
    push: (content: unknown) => act(() => handlers.get('trash')?.(content)),
    reconnect: () => act(() => onState?.('connected')),
  };
}

afterEach(cleanup);

describe('useTrash', () => {
  it('asks for the trash on mount and again on every reconnect', () => {
    const { client, sent, reconnect } = recordingClient();
    renderHook(() => useTrash(client));
    expect(sent).toEqual([{ method: 'ListTrash', parameters: undefined }]);

    reconnect();
    expect(sent.filter((frame) => frame.method === 'ListTrash')).toHaveLength(2);
  });

  it('holds the pushed entries and retention, and only then counts as loaded', () => {
    const { client, push } = recordingClient();
    const { result } = renderHook(() => useTrash(client));
    expect(result.current.loaded).toBe(false);

    push({ entries: [{ id: 'a', contentType: 'clip', fileName: 'a.mp4', deletedAt: 1, purgeAt: 2 }], retentionHours: 48 });

    expect(result.current.loaded).toBe(true);
    expect(result.current.retentionHours).toBe(48);
    expect(result.current.entries.map((entry) => entry.id)).toEqual(['a']);
  });

  it('ignores a frame that is not a trash message rather than blanking the list', () => {
    const { client, push } = recordingClient();
    const { result } = renderHook(() => useTrash(client));
    push({ entries: [{ id: 'a', contentType: 'clip', fileName: 'a.mp4', deletedAt: 1, purgeAt: 2 }] });
    push({ nonsense: true });
    expect(result.current.entries.map((entry) => entry.id)).toEqual(['a']);
  });

  it('sends the entry ids for restore and purge, and nothing for either when the list is empty', () => {
    const { client, sent } = recordingClient();
    const { result } = renderHook(() => useTrash(client));
    sent.length = 0;

    act(() => result.current.restore(['a', 'b']));
    act(() => result.current.purge(['a']));
    expect(sent).toEqual([
      { method: 'RestoreTrash', parameters: { entryIds: ['a', 'b'] } },
      { method: 'PurgeTrash', parameters: { entryIds: ['a'] } },
    ]);

    sent.length = 0;
    act(() => result.current.restore([]));
    act(() => result.current.purge([]));
    expect(sent).toEqual([]);
  });

  it('empties the trash with no parameters at all — that is what makes it mean "everything"', () => {
    const { client, sent } = recordingClient();
    const { result } = renderHook(() => useTrash(client));
    sent.length = 0;

    act(() => result.current.emptyTrash());
    expect(sent).toEqual([{ method: 'PurgeTrash', parameters: undefined }]);
  });
});
