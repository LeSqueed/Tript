// SPDX-License-Identifier: GPL-2.0-or-later

import { useLayoutEffect, useRef, useState } from 'react';
import type { ContentItem } from '../../ipc/protocol';
import { contentTypeLabel, formatContentDuration } from '../contentPresentation';
import { ContentThumbnail } from '../library/ContentThumbnail';
import { itemDuration, itemLabel } from '../library/libraryModel';
import { Icon } from '../ui/Icon';

const ROW_HEIGHT = 82;
const OVERSCAN = 4;
const DEFAULT_HEIGHT = 560;

function centeredScrollTop(itemCount: number, currentIndex: number, height: number): number {
  const maximum = Math.max(0, itemCount * ROW_HEIGHT - height);
  return Math.min(maximum, Math.max(0, currentIndex * ROW_HEIGHT - (height - ROW_HEIGHT) / 2));
}

export function playlistWindow(
  itemCount: number,
  scrollTop: number,
  viewportHeight: number,
): { start: number; end: number } {
  const visibleStart = Math.floor(Math.max(0, scrollTop) / ROW_HEIGHT);
  const visibleCount = Math.ceil(Math.max(ROW_HEIGHT, viewportHeight) / ROW_HEIGHT);
  const start = Math.max(0, visibleStart - OVERSCAN);
  return { start, end: Math.min(itemCount, visibleStart + visibleCount + OVERSCAN) };
}

export function PlaylistPanel({
  items,
  currentIndex,
  onSelect,
}: {
  items: ContentItem[];
  currentIndex: number;
  onSelect(index: number): void;
}) {
  const viewportRef = useRef<HTMLDivElement>(null);
  const [viewport, setViewport] = useState(() => ({
    scrollTop: centeredScrollTop(items.length, currentIndex, DEFAULT_HEIGHT),
    height: DEFAULT_HEIGHT,
  }));
  const window = playlistWindow(items.length, viewport.scrollTop, viewport.height);
  const [recenterToken, setRecenterToken] = useState(0);

  useLayoutEffect(() => {
    const element = viewportRef.current;
    if (!element) return;

    const height = element.clientHeight || DEFAULT_HEIGHT;
    const scrollTop = centeredScrollTop(items.length, currentIndex, height);
    element.scrollTop = scrollTop;
    setViewport({ scrollTop, height });
  }, [currentIndex, items.length, recenterToken]);

  return (
    <div className="player-panel-body">
      <div className="player-playlist-header">
        <button
          type="button"
          className="player-playlist-jump muted small"
          onClick={() => setRecenterToken((token) => token + 1)}
          aria-label="Jump to current item"
          title="Jump to current item"
        >
          {currentIndex + 1} of {items.length}
        </button>
      </div>
      <div
        ref={viewportRef}
        className="player-playlist-scroll"
        onScroll={(event) => setViewport({
          scrollTop: event.currentTarget.scrollTop,
          height: event.currentTarget.clientHeight || DEFAULT_HEIGHT,
        })}
      >
        <div className="player-playlist-window" style={{ height: items.length * ROW_HEIGHT }}>
          {items.slice(window.start, window.end).map((item, offset) => {
            const index = window.start + offset;
            const current = index === currentIndex;
            const label = itemLabel(item);
            const duration = formatContentDuration(itemDuration(item));
            return (
              <button
                key={`${item.contentType}:${item.filePath}`}
                type="button"
                className={current ? 'player-playlist-item active' : 'player-playlist-item'}
                style={{ top: index * ROW_HEIGHT }}
                onClick={() => onSelect(index)}
                aria-current={current ? 'true' : undefined}
                aria-label={`${current ? 'Playing' : 'Play'} ${label}`}
              >
                <span className="player-playlist-thumb">
                  <ContentThumbnail
                    filePath={item.filePath}
                    className="player-playlist-image"
                    fallback={<span className="player-playlist-placeholder"><Icon name="play" size={14} /></span>}
                  />
                </span>
                <span className="player-playlist-copy">
                  <span className="player-playlist-title" title={label}>{label}</span>
                  <span className="player-playlist-meta">
                    <span className="pill">{contentTypeLabel(item.contentType)}</span>
                    {duration && <span>{duration}</span>}
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      </div>
    </div>
  );
}
