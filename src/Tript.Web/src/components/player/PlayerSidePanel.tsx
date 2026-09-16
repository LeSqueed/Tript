// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect } from 'react';
import type { BookmarkItem, ContentItem } from '../../ipc/protocol';
import { PlaylistPanel } from './PlaylistPanel';
import { BookmarkPanel } from './BookmarkPanel';

export type PlayerPanelTab = 'playlist' | 'bookmarks';

export interface PlayerSidePanelProps {
  tab: PlayerPanelTab;
  onTabChange(tab: PlayerPanelTab): void;
  items: ContentItem[];
  currentIndex: number;
  onSelect(index: number): void;
  bookmarks: BookmarkItem[];
  currentTime: number;
  hiddenKinds: ReadonlySet<string>;
  onToggleKind(type: string): void;
  onSeek(time: number): void;
  onDeleteBookmark?(bookmark: BookmarkItem): void;
}

const TAB_LABELS: Record<PlayerPanelTab, string> = {
  playlist: 'Playlist',
  bookmarks: 'Bookmarks',
};

export function availableTabs(itemCount: number, bookmarkCount: number): PlayerPanelTab[] {
  const tabs: PlayerPanelTab[] = [];
  if (itemCount > 1) {
    tabs.push('playlist');
  }
  if (bookmarkCount > 0) {
    tabs.push('bookmarks');
  }
  return tabs;
}

export function PlayerSidePanel({
  tab,
  onTabChange,
  items,
  currentIndex,
  onSelect,
  bookmarks,
  currentTime,
  hiddenKinds,
  onToggleKind,
  onSeek,
  onDeleteBookmark,
}: PlayerSidePanelProps) {
  const tabs = availableTabs(items.length, bookmarks.length);
  const active = tabs.includes(tab) ? tab : tabs[0];

  useEffect(() => {
    if (active !== undefined && active !== tab) {
      onTabChange(active);
    }
  }, [active, tab, onTabChange]);

  if (active === undefined) {
    return null;
  }

  const groupLabel = tabs.length > 1 ? 'Playlist and bookmarks' : TAB_LABELS[tabs[0]];

  return (
    <aside className="player-playlist" aria-label={groupLabel}>
      <div className="player-panel-tabs" role="tablist" aria-label={groupLabel}>
        {tabs.map((candidate) => (
          <button
            key={candidate}
            type="button"
            role="tab"
            id={`player-panel-tab-${candidate}`}
            className={candidate === active ? 'player-panel-tab is-active' : 'player-panel-tab'}
            aria-selected={candidate === active}
            aria-controls="player-panel"
            onClick={() => onTabChange(candidate)}
          >
            {TAB_LABELS[candidate]}
          </button>
        ))}
      </div>
      <div
        className="player-panel"
        id="player-panel"
        role="tabpanel"
        aria-labelledby={`player-panel-tab-${active}`}
      >
        {active === 'playlist' ? (
          <PlaylistPanel items={items} currentIndex={currentIndex} onSelect={onSelect} />
        ) : (
          <BookmarkPanel
            bookmarks={bookmarks}
            currentTime={currentTime}
            hiddenKinds={hiddenKinds}
            onToggleKind={onToggleKind}
            onSeek={onSeek}
            onDelete={onDeleteBookmark}
          />
        )}
      </div>
    </aside>
  );
}
