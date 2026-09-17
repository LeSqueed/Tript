// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useMemo, useState, type Dispatch, type SetStateAction } from 'react';
import type { ContentItem } from '../../ipc/protocol';

export function usePlaylistItem(navigation: ContentItem[], requestedItem: ContentItem | undefined): {
  item: ContentItem | undefined;
  itemIndex: number;
  setItemIndex: Dispatch<SetStateAction<number>>;
} {
  const [itemIndex, setItemIndex] = useState(() => {
    const initialIndex = requestedItem
      ? navigation.findIndex((candidate) => candidate.filePath === requestedItem.filePath)
      : -1;
    return initialIndex >= 0 ? initialIndex : 0;
  });

  useEffect(() => {
    if (itemIndex >= navigation.length) {
      setItemIndex(0);
    }
  }, [itemIndex, navigation.length]);

  const requestedIndex = useMemo(
    () => (requestedItem ? navigation.findIndex((s) => s.filePath === requestedItem.filePath) : -1),
    [requestedItem, navigation],
  );
  useEffect(() => {
    if (requestedIndex >= 0) {
      setItemIndex(requestedIndex);
    }
  }, [requestedIndex]);

  const item: ContentItem | undefined =
    requestedIndex >= 0 && itemIndex === requestedIndex
      ? navigation[requestedIndex]
      : requestedIndex < 0
        ? (requestedItem ?? navigation[itemIndex] ?? navigation[0])
        : (navigation[itemIndex] ?? navigation[0]);

  return { item, itemIndex, setItemIndex };
}
