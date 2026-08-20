// SPDX-License-Identifier: GPL-2.0-or-later
//
// One card in the library grid: a 16:9 thumbnail, the title, and chips for what the item is, which
// game it came from, when it was recorded and how big it is.
//
// The card's *open* affordance is a `<button>` rather than a div with a click handler, so it is
// keyboard-reachable and activatable for free (Enter/Space) and the player overlay can restore focus
// to it on close. The select checkbox and the delete button are its SIBLINGS inside a positioned
// wrapper, not its children: a button inside a button is invalid markup and browsers disagree about
// which one a click activates.

import { useEffect, useState } from 'react';
import type { ContentItem } from '../../ipc/protocol';
import { thumbnailUrl } from '../../ipc/endpoints';
import {
  formatDateChip,
  formatDurationChip,
  formatSizeChip,
  itemGame,
  itemLabel,
  typeLabel,
  UNKNOWN_GAME_LABEL,
} from './libraryModel';
import { Icon } from '../ui/Icon';
import { Checkbox } from '../../components/ui/controls';

export function ContentCard({
  item,
  onOpen,
  onDelete,
  onToggleFavorite,
  selectable = false,
  selected = false,
  onToggleSelected,
}: {
  item: ContentItem;
  /** The library's open seam: called with the item the user activated. */
  onOpen?: (item: ContentItem) => void;
  /** The library's delete seam. Absent means the card offers no delete at all. */
  onDelete?: (item: ContentItem) => void;
  onToggleFavorite?: (item: ContentItem) => void;
  /** Whether the grid is in selection mode — the checkbox only exists then. */
  selectable?: boolean;
  selected?: boolean;
  onToggleSelected?: (item: ContentItem) => void;
}) {
  // Per-card, per-path: a `content` push can replace the item under this card (a rename keeps the
  // path, a delete + re-record does not), and a previous path's failure must not condemn the new one.
  const [thumbnailFailed, setThumbnailFailed] = useState(false);
  useEffect(() => {
    setThumbnailFailed(false);
  }, [item.filePath]);

  const label = itemLabel(item);
  const game = itemGame(item) ?? UNKNOWN_GAME_LABEL;
  const duration = formatDurationChip(item);
  const size = formatSizeChip(item);
  // An item with no path has nothing to ask the content server for — straight to the placeholder,
  // rather than a request that is guaranteed to fail.
  const showThumbnail = item.filePath.length > 0 && !thumbnailFailed;

  return (
    <div className={selected ? 'content-card-shell selected' : 'content-card-shell'}>
      <button
        type="button"
        className="content-card"
        data-testid="content-card"
        onClick={() => onOpen?.(item)}
        aria-label={`Open ${label}`}
      >
        <span className="content-card-thumb">
          {showThumbnail ? (
            <img
              className="content-card-image"
              src={thumbnailUrl(item.filePath)}
              // Decorative: the title sits right beside it, so describing the frame again would only
              // make a screen reader say the same name twice.
              alt=""
              loading="lazy"
              decoding="async"
              width={480}
              height={270}
              onError={() => setThumbnailFailed(true)}
            />
          ) : (
            <span className="content-card-placeholder" data-testid="content-card-placeholder">
              <Icon name="play" size={22} />
            </span>
          )}
          {duration !== null && <span className="content-card-duration">{duration}</span>}
        </span>
        <span className="content-card-body">
          <span className="content-card-title" title={label}>
            {label}
          </span>
          <span className="content-card-chips">
            <span className="pill content-card-type">{typeLabel(item)}</span>
            <span className="pill pill-muted">{game}</span>
            <span className="pill pill-muted">{formatDateChip(item)}</span>
            {size !== null && <span className="pill pill-muted">{size}</span>}
          </span>
        </span>
      </button>

      {selectable && (
        <Checkbox
          className="content-card-select"
          checked={selected}
          aria-label={`Select ${label}`}
          onChange={() => onToggleSelected?.(item)}
        />
      )}

      {onDelete && (
        <button
          type="button"
          className="content-card-delete"
          onClick={() => onDelete(item)}
          aria-label={`Delete ${label}`}
        >
          <Icon name="trash" size={15} />
        </button>
      )}

      {onToggleFavorite && (
        <button
          type="button"
          className={item.favorite ? 'content-card-favorite active' : 'content-card-favorite'}
          onClick={() => onToggleFavorite(item)}
          aria-label={`${item.favorite ? 'Remove' : 'Add'} ${label} ${item.favorite ? 'from' : 'to'} favorites`}
          aria-pressed={item.favorite === true}
        >
          <Icon name="star" size={15} filled={item.favorite === true} />
        </button>
      )}
    </div>
  );
}
