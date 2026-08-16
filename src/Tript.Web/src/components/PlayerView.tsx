// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player + dual synced timeline.
//
// The dual timeline is the heart of the session-review experience: a thin full-session bar with
// bookmark ticks and a zoomed-in precision timeline, both driven by the same playhead. Progress is
// synced by construction — both timelines render `currentTime` from `usePlayback`, which the video
// element drives; a click on either timeline seeks directly. The zoom window is kept centred on
// the playhead (the zoom model in player/timelineModel.ts).
//
// Bookmarks are icons at their timestamps: ticks on the full-session bar, detail icons with a
// hover bubble on the zoomed timeline; a click jumps to the bookmark's time.
//
// Navigation (previous/next) moves between sessions in the current context, wrapping at both ends.
//
// Data comes from the player session source seam (player/sessionSource.ts) — the alpha stub. The
// real IPC-backed source plugs in behind the same interface. Region selection is exposed to T9
// (the clip dialog) through player/clipSeam.ts; this view deliberately builds no dialog.

import { useCallback, useEffect, useMemo, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { BookmarkItem, ContentItem } from '../ipc/protocol';
import { contentUrl } from '../ipc/endpoints';
import { DEFAULT_SESSION_SECONDS, stubSessionSource, type SessionSource } from './player/sessionSource';
import type { TimelineRegion } from './player/clipSeam';
import { clampWindow, zoomWindow, DEFAULT_WINDOW_SECONDS, type WindowState } from './player/timelineModel';
import { usePlayback } from './player/usePlayback';
import { FullSessionBar } from './player/FullSessionBar';
import { ZoomedTimeline } from './player/ZoomedTimeline';
import { TransportBar } from './player/TransportBar';

export interface PlayerViewProps {
  client: IpcClient;
  /** The session source seam. Defaults to the alpha stub; the real source plugs in here. */
  source?: SessionSource;
  /** The clip-dialog seam. The alpha ships with no regions. */
  regions?: TimelineRegion[];
  selectedRegionId?: string | null;
  onRegionSelect?(region: TimelineRegion): void;
}

const sessionSource: SessionSource = stubSessionSource;

export function PlayerView({
  client,
  source = sessionSource,
  regions = [],
  selectedRegionId = null,
  onRegionSelect = () => {},
}: PlayerViewProps) {
  const sessions = useMemo(() => source.getSessions(), [source]);
  const [itemIndex, setItemIndex] = useState(0);
  const item: ContentItem = sessions[itemIndex] ?? sessions[0] ?? sessionSource.getSessions()[0];
  const bookmarks = useMemo(() => (item ? source.getBookmarks(item) : []), [source, item]);

  const fallbackDuration =
    item?.endTime !== undefined && item.endTime > 0 ? item.endTime : DEFAULT_SESSION_SECONDS;
  const playback = usePlayback(item?.filePath ?? '', fallbackDuration);
  const { duration, currentTime, seek, videoRef } = playback;

  // NOTE: the state variable is `viewWindow`, never `window` — `window` is the DOM global and
  // shadowing it would break the keyboard listener below (addEventListener on a WindowState).
  const [viewWindow, setViewWindow] = useState<WindowState>(() =>
    zoomWindow(0, DEFAULT_WINDOW_SECONDS, duration),
  );

  // Keep the zoom window inside the session and centred on the playhead. The playhead always stays
  // visible as the session plays; a deliberate window pan/zoom by the user is not fought.
  useEffect(() => {
    setViewWindow((prev) => zoomWindow(currentTime, prev.seconds, duration));
  }, [currentTime, duration]);

  const navigate = useCallback(
    (delta: number) => {
      if (sessions.length === 0) {
        return;
      }
      setItemIndex((index) => (index + delta + sessions.length) % sessions.length);
    },
    [sessions.length],
  );

  const toggleFullscreen = useCallback(() => {
    client.send('ToggleFullscreen', { enabled: true });
  }, [client]);

  // Keyboard: space toggles play/pause, arrows seek (per the navigation spec).
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      if (event.target instanceof HTMLElement && event.target.closest('input, textarea, button, [role="slider"]')) {
        return;
      }
      if (event.code === 'Space') {
        event.preventDefault();
        playback.togglePlayPause();
      } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        seek(currentTime - 5);
      } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        seek(currentTime + 5);
      }
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [playback, currentTime, seek]);

  if (!item) {
    return (
      <section className="player-view">
        <p className="muted">No sessions.</p>
      </section>
    );
  }

  const videoSrc = contentUrl(item.filePath);

  return (
    <section className="player-view">
      <div className="player-controls-row">
        <button
          type="button"
          className="btn ghost"
          onClick={() => navigate(-1)}
          disabled={sessions.length <= 1}
          aria-label="Previous session"
        >
          ← Prev
        </button>
        <span className="player-title" data-testid="player-title">
          {item.title ?? item.fileName}
        </span>
        <button
          type="button"
          className="btn ghost"
          onClick={() => navigate(1)}
          disabled={sessions.length <= 1}
          aria-label="Next session"
        >
          Next →
        </button>
      </div>

      <div className="video-frame">
        <video
          ref={videoRef}
          className="video-element"
          controls
          src={videoSrc}
          onTimeUpdate={(event) => playback.onVideoTimeUpdate(event.currentTarget.currentTime)}
          onDurationChange={(event) => playback.onVideoDuration(event.currentTarget.duration)}
          onPlay={() => {
            playback.onVideoTimeUpdate(videoRef.current?.currentTime ?? 0);
            playback.onVideoPlay();
          }}
          onPause={() => {
            playback.onVideoTimeUpdate(videoRef.current?.currentTime ?? 0);
            playback.onVideoPause();
          }}
          onEnded={playback.onVideoEnded}
        />
      </div>

      <TransportBar
        playing={playback.playing}
        currentTime={currentTime}
        duration={duration}
        onTogglePlayPause={playback.togglePlayPause}
        onToggleFullscreen={toggleFullscreen}
      />

      <div className="timeline-stack">
        <FullSessionBar
          currentTime={currentTime}
          duration={duration}
          bookmarks={bookmarks}
          window={clampWindow(viewWindow, duration)}
          onSeek={seek}
        />
        <ZoomedTimeline
          currentTime={currentTime}
          duration={duration}
          window={clampWindow(viewWindow, duration)}
          bookmarks={bookmarks}
          regions={regions}
          selectedRegionId={selectedRegionId}
          onWindowChange={setViewWindow}
          onSeek={seek}
          onRegionSelect={onRegionSelect}
        />
      </div>

      <div className="player-footer">
        <span className="muted small">
          Bookmarks: {bookmarks.length}
        </span>
        <span className="muted small">
          Zoom window: {viewWindow.seconds.toFixed(1)}s
        </span>
      </div>
    </section>
  );
}

export type { BookmarkItem };
