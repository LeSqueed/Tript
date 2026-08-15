// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player + timeline. In the alpha this is a stub: the player element with a full-session
// progress bar and a zoomed timeline placeholder, next/previous navigation between items, and a
// live IPC probe (content URLs from the content server). The real timeline, bookmarks and
// clipping land in later tasks.

import { useState } from 'react';
import { contentUrl } from '../ipc/endpoints';

export function PlayerView() {
  const [item, setItem] = useState(0);
  const total = 12; // session count placeholder
  const previous = () => setItem((i) => (i - 1 + total) % total);
  const next = () => setItem((i) => (i + 1) % total);

  return (
    <section className="player-view">
      <div className="player-controls-row">
        <button type="button" className="btn ghost" onClick={previous} aria-label="Previous">
          ← Prev
        </button>
        <span className="player-title">Session {item + 1}</span>
        <button type="button" className="btn ghost" onClick={next} aria-label="Next">
          Next →
        </button>
      </div>

      <div className="video-frame">
        <video className="video-element" controls src={contentUrl('placeholder.mp4')} />
        <p className="muted small">
          Content server: {contentUrl('placeholder.mp4')}
        </p>
      </div>

      <div className="timeline-stub">
        <div className="timeline-progress">
          <div className="timeline-progress-fill" style={{ width: '35%' }} />
        </div>
        <p className="muted small">Zoomed event timeline lands in the timeline task.</p>
      </div>
    </section>
  );
}
