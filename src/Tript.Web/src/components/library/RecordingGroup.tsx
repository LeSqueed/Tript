// SPDX-License-Identifier: GPL-2.0-or-later
//
// One recording and the clips cut from it.
//
// The library is recording-centric because that is the shape of the work: you play, Tript records,
// and then you cut the moments worth keeping out of what it caught. A flat grid put a recording and
// its own clips in arbitrary positions relative to each other, sorted by time like strangers.
//
// Recent groups form a compact landing shelf. Every other group renders as an archive row.

import type { ContentItem } from '../../ipc/protocol';
import type { RecordingGroup as Group } from './libraryModel';
import { ContentCard } from './ContentCard';

export interface GroupActions {
  onOpen?: (item: ContentItem) => void;
  onDelete?: (item: ContentItem) => void;
  onToggleFavorite?: (item: ContentItem) => void;
  selectable?: boolean;
  isSelected?: (item: ContentItem) => boolean;
  onToggleSelected?: (item: ContentItem) => void;
}

function cardFor(
  item: ContentItem,
  actions: GroupActions,
  variant: 'grid' | 'wide' = 'grid',
  priority = false,
  clipsCount = 0,
  highlightsCount = 0,
) {
  return (
    <ContentCard
      item={item}
      variant={variant}
      priority={priority}
      clipsCount={clipsCount}
      highlightsCount={highlightsCount}
      onOpen={actions.onOpen}
      onDelete={actions.onDelete}
      onToggleFavorite={actions.onToggleFavorite}
      selectable={actions.selectable}
      selected={actions.isSelected?.(item) ?? false}
      onToggleSelected={actions.onToggleSelected}
    />
  );
}

export function RecordingGroup({
  group,
  variant,
  actions,
}: {
  group: Group;
  variant: 'hero' | 'recent' | 'row';
  actions: GroupActions;
}) {
  const recording = group.recording;
  const automaticClips = group.clips.filter((clip) => clip.automated);

  if ((variant === 'hero' || variant === 'recent') && recording) {
    return (
      <section
        className={variant === 'recent' ? 'recording-group recording-group--recent' : 'library-hero'}
        data-testid={variant === 'recent' ? 'recording-group-recent' : 'library-hero'}
        aria-label={variant === 'recent' ? 'Recent recording' : 'Newest recording'}
      >
        {cardFor(
            recording,
            actions,
            variant === 'recent' ? 'grid' : 'wide',
            variant === 'recent',
           group.clips.length,
           automaticClips.length,
        )}
      </section>
    );
  }

  // A clip whose recording is gone. It renders as itself, with no head and no explanation: that the
  // source was deleted is not something the user can act on, and a card that announces it would draw
  // the eye to the one item on the screen that needs nothing.
  if (!recording) {
    return null;
  }

  return (
    <section className="recording-group" data-testid="recording-group">
        <div className="recording-group-body">
          <div className="recording-group-head">
            {cardFor(recording, actions, 'grid', false, group.clips.length, automaticClips.length)}
          </div>
      </div>
    </section>
  );
}
