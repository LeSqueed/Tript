// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clips page. Clips are read from the shared IPC session source (owned by the App shell) the
// same way the library reads sessions — the source splits the list by contentType. Clicking a clip
// opens it in the player via the shell's `onOpen` seam.

import type { ContentItem } from '../ipc/protocol';

export function ClipsView({
  clips,
  onOpen,
}: {
  /** The clip list, already filtered and reactive (owned by the App shell's source). */
  clips: ContentItem[];
  /** The App shell's player seam: called with the clip the user clicked. */
  onOpen?: (item: ContentItem) => void;
}) {
  return (
    <section className="panel clips-view">
      <h2>Clips</h2>
      {clips.length === 0 ? (
        <p className="muted">Your clips will appear here.</p>
      ) : (
        <ul className="content-list" data-testid="clips-list">
          {clips.map((item) => (
            <li key={item.filePath}>
              <button
                type="button"
                className="content-row"
                onClick={() => onOpen?.(item)}
                aria-label={`Open ${item.title ?? item.fileName}`}
              >
                <span className="content-name">{item.title ?? item.fileName}</span>
                <span className="content-source muted small">{item.filePath}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
