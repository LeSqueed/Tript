// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (c) 2026 LeSqueed and the Tript contributors

import { useEffect, useState } from 'react';
import type { ContentItem, RecordingState } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';
import { Button } from './ui/controls';
import { ContentCard } from './library/ContentCard';
import { itemLabel, orderedHighlights } from './library/libraryModel';

export function SessionClipsView({
  recording,
  clips,
  client,
  onBack,
  backLabel,
  onOpen,
  onToggleFavorite,
  onDelete,
}: {
  recording: ContentItem;
  clips: ContentItem[];
  client: IpcClient;
  onBack(): void;
  backLabel: string;
  onOpen(
    item: ContentItem,
    navigation: ContentItem[],
    rebuild?: (items: readonly ContentItem[]) => ContentItem[],
  ): void;
  onToggleFavorite(item: ContentItem): void;
  onDelete(item: ContentItem): void;
}) {
  const [automaticClips, setAutomaticClips] = useState<RecordingState['automaticClips']>(null);

  useEffect(() => client.on('state', (content) => {
    const state = (content as { state?: RecordingState }).state;
    const job = state?.automaticClips;
    setAutomaticClips(job?.sourceSessionPath === recording.filePath ? job : null);
  }), [client, recording.filePath]);

  const ordered = [...clips].sort(
    (left, right) => (left.clipStartTime ?? Number.POSITIVE_INFINITY) - (right.clipStartTime ?? Number.POSITIVE_INFINITY),
  );
  const processing = recording.automaticClipsProcessing === true || automaticClips?.active === true;
  const paused = recording.automaticClipsPaused === true || automaticClips?.paused === true;
  const completed = automaticClips?.completed ?? recording.automaticClipsCompleted;
  const total = automaticClips?.total ?? recording.automaticClipsTotal;

  return (
    <section className="session-clips-view" aria-labelledby="session-clips-title">
      <div className="session-clips-header">
        <Button variant="ghost" size="small" icon="chevronLeft" onClick={onBack}>
          {backLabel}
        </Button>
        <div>
          <p className="muted small">Automated clips</p>
          <h1 id="session-clips-title">{itemLabel(recording)}</h1>
        </div>
        <span className="session-clips-count">{ordered.length} highlight{ordered.length === 1 ? '' : 's'}</span>
      </div>

      {processing && (
        <p className="session-clips-processing" role="status">
          {paused ? 'Highlights paused' : 'Creating highlights'}
          {typeof completed === 'number' && typeof total === 'number' && ` (${completed}/${total})`}
        </p>
      )}

      {ordered.length === 0 ? (
        <div className="panel session-clips-empty">
          <h2>No automated clips yet</h2>
          <p className="muted">No highlights have been created for this recording yet.</p>
        </div>
      ) : (
        <div className="library-grid session-clips-grid">
          {ordered.map((clip) => (
            <ContentCard
              key={clip.filePath}
              item={clip}
              onOpen={(selected) => onOpen(selected, ordered, (next) => orderedHighlights(recording.filePath, next))}
              onToggleFavorite={onToggleFavorite}
              onDelete={onDelete}
            />
          ))}
        </div>
      )}
    </section>
  );
}
