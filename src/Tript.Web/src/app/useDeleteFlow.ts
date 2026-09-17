// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useRef, useState } from 'react';
import { makeDeleteConfirmation, type DeleteConfirmation } from '../components/library/ConfirmDeleteDialog';
import { cascadableLinkedHighlights, itemLabel } from '../components/library/libraryModel';
import type { TrashController } from '../components/trash/useTrash';
import { useToast } from '../components/ui/toast/ToastProvider';
import type { ContentItem } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';
import type { AppNavigation } from './useAppNavigation';

export interface DeleteFlow {
  deleteConfirmation: DeleteConfirmation | null;
  requestPlayerDelete: (item: ContentItem) => void;
  requestSessionDelete: (item: ContentItem) => void;
  confirmDelete: (permanent: boolean, deleteLinkedHighlights: boolean) => void;
  cancelDelete: () => void;
}

export function useDeleteFlow({
  client,
  items,
  trash,
  navigation,
  deleteLinkedHighlightsByDefault,
}: {
  client: IpcClient;
  items: ContentItem[];
  trash: TrashController;
  navigation: AppNavigation;
  deleteLinkedHighlightsByDefault: boolean;
}): DeleteFlow {
  const { push, dismiss } = useToast();
  const [pendingDelete, setPendingDelete] = useState<{ item: ContentItem; advancePlayer: boolean } | null>(null);
  const [pendingRestore, setPendingRestore] = useState<ContentItem | null>(null);
  const restoreSentRef = useRef(false);
  const {
    advanceAfterPlayerDelete,
    playerReturnRoute,
    sessionReview,
    openSessionClip,
    openInPlayer,
  } = navigation;

  const deleteItem = useCallback((
    item: ContentItem,
    permanent: boolean,
    deleteLinkedHighlights: boolean,
    advancePlayer: boolean,
  ) => {
    client.send('DeleteContent', {
      contentType: item.contentType,
      fileName: item.filePath,
      ...(permanent ? { permanent: true } : {}),
      ...(item.contentType === 'recording' && deleteLinkedHighlights
        ? { deleteLinkedHighlights: true }
        : {}),
    });
    if (advancePlayer) {
      advanceAfterPlayerDelete(item);
    }
  }, [client, advanceAfterPlayerDelete]);

  const beginRestore = useCallback((item: ContentItem) => {
    restoreSentRef.current = false;
    setPendingRestore(item);
  }, []);

  const requestPlayerDelete = useCallback((item: ContentItem) => {
    if (playerReturnRoute === 'session') {
      deleteItem(item, false, false, true);
      push({
        key: `trashed-${item.filePath}`,
        kind: 'success',
        message: `Moved "${itemLabel(item)}" to trash.`,
        duration: 10_000,
        actions: [{ label: 'Restore', onClick: () => beginRestore(item) }],
      });
      return;
    }
    setPendingDelete({ item, advancePlayer: true });
  }, [deleteItem, playerReturnRoute, push, beginRestore]);

  const requestSessionDelete = useCallback((item: ContentItem) => {
    setPendingDelete({ item, advancePlayer: false });
  }, []);

  const confirmDelete = useCallback((permanent: boolean, deleteLinkedHighlights: boolean) => {
    if (pendingDelete) {
      deleteItem(
        pendingDelete.item,
        permanent,
        deleteLinkedHighlights,
        pendingDelete.advancePlayer,
      );
    }
    setPendingDelete(null);
  }, [deleteItem, pendingDelete]);

  const cancelDelete = useCallback(() => setPendingDelete(null), []);

  useEffect(() => {
    if (pendingRestore === null || restoreSentRef.current) {
      return;
    }
    const entry = trash.entries.find(
      (candidate) =>
        candidate.contentType === pendingRestore.contentType
        && candidate.fileName === pendingRestore.fileName,
    );
    if (entry !== undefined) {
      restoreSentRef.current = true;
      trash.restore([entry.id]);
    }
  }, [pendingRestore, trash]);

  useEffect(() => {
    if (pendingRestore === null || !restoreSentRef.current) {
      return;
    }
    const restored = items.find((candidate) => candidate.filePath === pendingRestore.filePath);
    if (restored !== undefined) {
      setPendingRestore(null);
      dismiss(`trashed-${restored.filePath}`);
      if (sessionReview !== null
        && restored.automated === true
        && restored.sourceSessionPath === sessionReview.recording.filePath) {
        const clips = items
          .filter((candidate) => candidate.automated && candidate.sourceSessionPath === sessionReview.recording.filePath)
          .sort((left, right) => (left.clipStartTime ?? Number.POSITIVE_INFINITY) - (right.clipStartTime ?? Number.POSITIVE_INFINITY));
        openSessionClip(restored, clips);
      } else {
        openInPlayer(restored, items);
      }
    }
  }, [pendingRestore, items, dismiss, openInPlayer, openSessionClip, sessionReview]);

  const deleteConfirmation = pendingDelete
    ? describeDelete(pendingDelete.item, items, trash.retentionHours, deleteLinkedHighlightsByDefault)
    : null;

  return { deleteConfirmation, requestPlayerDelete, requestSessionDelete, confirmDelete, cancelDelete };
}

function describeDelete(
  item: ContentItem,
  items: ContentItem[],
  retentionHours: number,
  deleteLinkedHighlightsByDefault: boolean,
): DeleteConfirmation {
  const isRecording = item.contentType === 'recording';
  const placeholder = isRecording && (item.videoMissing === true || item.highlightsOnly === true);
  return makeDeleteConfirmation({
    names: [itemLabel(item)],
    retentionHours,
    affectedCount: placeholder ? 0 : 1,
    hasCascade: isRecording,
    cascadeCount: isRecording ? cascadableLinkedHighlights(item, items).length : 0,
    deleteLinkedHighlightsDefault: deleteLinkedHighlightsByDefault,
    title: `Delete ${itemLabel(item)}?`,
  });
}
