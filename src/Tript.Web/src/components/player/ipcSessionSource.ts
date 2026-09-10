// SPDX-License-Identifier: GPL-2.0-or-later

import type { IpcClient } from '../../ipc/websocketClient';
import type { BookmarkItem, ContentItem } from '../../ipc/protocol';
import type { SessionSource } from './sessionSource';

interface ContentMessageContent {
  content: ContentItem[];
}

export interface IpcSessionSource extends SessionSource {
  getClips(): ContentItem[];
  getItems(): ContentItem[];
  getVersion(): number;
  dispose(): void;
}

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

  client.send('ListContent');
  const unsubscribeContent = client.on('content', applyContent);
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
      return items.filter((item) => item.contentType === 'clip' || item.contentType === 'highlight');
    },
    getItems(): ContentItem[] {
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
