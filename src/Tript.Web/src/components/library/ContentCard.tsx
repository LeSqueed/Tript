// SPDX-License-Identifier: GPL-2.0-or-later
//
// One card in the library grid: a 16:9 thumbnail, the title, and chips for what the item is, which
// game it came from, when it was recorded and how big it is.
//
// The card is a `<button>` and not a div with a click handler, so it is keyboard-reachable and
// activatable for free (Enter/Space), and the overlay it opens can restore focus to it on close —
// a div would need a tabindex and a key handler to get the same thing half right. Nothing inside is
// interactive, which is what keeps the nesting valid.
//
// THE THUMBNAIL SEAM. `GET /api/thumbnail/<filePath>` answers with a 480x270 JPEG when one exists and
// **204 No Content** when one does not (an extraction that failed, a file the backend has not got to
// yet, an older backend with no thumbnail store at all). A 204 leaves the `<img>` with no image data,
// so the browser fires `error` — which is the only signal the element gives us, and is therefore what
// swaps in the placeholder tile. The card never waits on the thumbnail to render its text: a library
// of items with no thumbnails is a grid of placeholders, not an empty page.
//
// Images are `loading="lazy"` because a library is unbounded — a thousand cards must not become a
// thousand requests the moment the grid mounts. The intrinsic 480x270 is declared on the element so
// the card reserves its space before the bytes arrive and the grid does not reflow as they land.

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

export function ContentCard({
  item,
  onOpen,
}: {
  item: ContentItem;
  /** The library's open seam: called with the item the user activated. */
  onOpen?: (item: ContentItem) => void;
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
          <span className="content-card-placeholder" data-testid="content-card-placeholder" aria-hidden="true">
            ▶
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
  );
}
