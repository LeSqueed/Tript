// SPDX-License-Identifier: GPL-2.0-or-later
//
// The IPC-backed session source. Sits on the control socket's `content` push:
//
//   - `ListContent` is sent on creation. The backend does NOT include content in its NewConnection
//     push, so this is how the frontend asks for the initial list; the backend answers by pushing a
//     `content` message.
//   - Every `content` push (initial answer, plus later ones after StopRecording / CreateClip done /
//     delete / rename) replaces the source's item list and notifies subscribers.
//   - On a (re)connect the source re-sends `ListContent`: the content list is not part of the
//     NewConnection push, so a socket drop would otherwise leave the source empty until the next
//     content change.
//
// The source implements the `SessionSource` seam plus clip access and a monotonic `getVersion()` —
// the reactivity hook consumers use (e.g. via `useSyncExternalStore` with `observeSessions` as
// `subscribe` and `getVersion` as `getSnapshot`), so a push re-renders the list even though the
// source object is stable.

import type { IpcClient } from '../../ipc/websocketClient';
import type { BookmarkItem, ContentItem } from '../../ipc/protocol';
import type { SessionSource } from './sessionSource';

/** The `content` message content on the wire: the full content list. */
interface ContentMessageContent {
  content: ContentItem[];
}

export interface IpcSessionSource extends SessionSource {
  /** Clips (contentType === 'clip') in the same list order. */
  getClips(): ContentItem[];
  /** The whole pushed list, in the backend's order — what the library grid renders over. */
  getItems(): ContentItem[];
  /** Monotonic version, incremented on every `content` push. The reactivity hook. */
  getVersion(): number;
  /** Drop the `content` and reconnect subscriptions (for tests / teardown). */
  dispose(): void;
}

/**
 * Create a session source driven by the control socket. Sends `ListContent` immediately, then
 * stores every `content` push and notifies subscribers.
 */
export function createIpcSessionSource(client: IpcClient): IpcSessionSource {
  let items: ContentItem[] = [];
  let version = 0;
  const subscribers = new Set<() => void>();

  function notify(): void {
    for (const subscriber of [...subscribers]) {
      subscriber();
    }
  }

  function applyContent(content: unknown): void {
    const message = content as ContentMessageContent;
    if (!message || !Array.isArray(message.content)) {
      return;
    }
    items = message.content;
    version += 1;
    notify();
  }

  // Ask for the initial list. The backend answers with a `content` push (it is not part of the
  // NewConnection push), so the source is empty until that answer arrives.
  client.send('ListContent');
  const unsubscribeContent = client.on('content', applyContent);
  // A (re)connect must re-ask: the content list is not pushed on NewConnection. This also covers
  // the case where the source is created while the socket is still dialing — the first 'connected'
  // state transition sends the ListContent that the pre-open send dropped.
  const unsubscribeState = client.onStateChange((state) => {
    if (state === 'connected') {
      client.send('ListContent');
    }
  });

  return {
    getSessions(): ContentItem[] {
      return items.filter((item) => item.contentType === 'recording');
    },
    getClips(): ContentItem[] {
      return items.filter((item) => item.contentType === 'clip');
    },
    getItems(): ContentItem[] {
      // The pushed array itself, not a copy: consumers treat it as immutable (the library sorts into
      // a new array), and copying it on every read would defeat the version-keyed memoisation the
      // reactive hook relies on.
      return items;
    },
    getBookmarks(item: ContentItem): BookmarkItem[] {
      return item.bookmarks ?? [];
    },
    getVersion(): number {
      return version;
    },
    observeSessions(onChange: () => void): () => void {
      subscribers.add(onChange);
      return () => {
        subscribers.delete(onChange);
      };
    },
    dispose(): void {
      unsubscribeContent();
      unsubscribeState();
      subscribers.clear();
    },
  };
}
