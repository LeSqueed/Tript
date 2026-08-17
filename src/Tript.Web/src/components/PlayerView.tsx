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
// Data comes from the player session source seam (player/sessionSource.ts). The default source is
// IPC-backed (player/ipcSessionSource.ts) and re-renders on the control socket's `content` push;
// tests inject their own static source through the `source` prop.
//
// This view is the seam owner for the clip dialog (T9): it renders the dialog in the player,
// feeds it the session's regions, and wires the `importProgress` message (the clip result arrives
// asynchronously — the backend never returns from CreateClip synchronously) and the `state` message
// (per-track audio layout). The segment-looping affordance lives here too: while the playhead is
// inside the selected marked segment and crosses its end, playback seeks back to the segment's
// start — stay inside a marked segment and it loops; leave it and normal playback resumes
// (spec/frontend.md — "segment looping").

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { BookmarkItem, ContentItem, RecordingState } from '../ipc/protocol';
import { contentUrl } from '../ipc/endpoints';
import { DEFAULT_SESSION_SECONDS, type SessionSource } from './player/sessionSource';
import { useIpcSessionSource, useSessionSource } from './player/useSessionSource';
import type { TimelineRegion } from './player/clipSeam';
import { clampWindow, zoomWindow, DEFAULT_WINDOW_SECONDS, type WindowState } from './player/timelineModel';
import { usePlayback } from './player/usePlayback';
import { FullSessionBar } from './player/FullSessionBar';
import { ZoomedTimeline } from './player/ZoomedTimeline';
import { TransportBar } from './player/TransportBar';
import { useClipDialog } from './player/useClipDialog';
import { ClipDialog } from './player/clipDialog';
import { computeLoopDecision } from './player/clipLoop';
import { clampTime } from './player/clipModel';

export interface PlayerViewProps {
  client: IpcClient;
  /**
   * The session source seam. When the App shell passes its shared source, that is used; when the
   * source is null (the shell's source is created in an effect) or absent, the player owns an
   * IPC-backed source of its own. Tests inject their own static source through this prop.
   */
  source?: SessionSource | null;
  /**
   * The item to open (from a library/clips click). When present the player starts on that item and
   * its index within the session list becomes the navigation base. Absent, the first session plays.
   */
  item?: ContentItem;
  /**
   * The clip-dialog seam. When a session is playing, the player opens its own dialog that owns
   * the region list; a caller can alternatively supply regions for a read-only region view.
   * Defaults to the dialog's own regions (none before the dialog is opened).
   */
  regions?: TimelineRegion[];
  selectedRegionId?: string | null;
  onRegionSelect?(region: TimelineRegion): void;
}

export function PlayerView({
  client,
  source: injectedSource,
  item: requestedItem,
  regions: externalRegions,
  selectedRegionId: externalSelectedRegionId,
  onRegionSelect: externalOnRegionSelect,
}: PlayerViewProps) {
  // No injected source → own an IPC-backed one for this view's lifetime. It sends ListContent on
  // creation and re-reads the `content` push, so the list is live. Tests that inject their own
  // source opt out of the IPC source entirely (`enabled = false` — no stray ListContent). A null
  // injected source is the shell's shared source that has not been created in its effect yet — the
  // player waits for it rather than creating a second IPC source (which would double-ask the
  // backend for the list).
  const ipcSource = useIpcSessionSource(client, injectedSource === undefined);
  const source = injectedSource ?? ipcSource;
  const { sessions } = useSessionSource(source);
  const [itemIndex, setItemIndex] = useState(0);

  // Keep the selection in range when the content list changes (a `content` push can remove or
  // reorder sessions): clamp back to the first session, matching the fallback below.
  useEffect(() => {
    if (itemIndex >= sessions.length) {
      setItemIndex(0);
    }
  }, [itemIndex, sessions.length]);

  // The requested item's position in the session list, when it is one of the sessions. Once it is,
  // it becomes the navigation base; navigating away moves within the list as usual.
  const requestedIndex = useMemo(
    () => (requestedItem ? sessions.findIndex((s) => s.filePath === requestedItem.filePath) : -1),
    [requestedItem, sessions],
  );
  useEffect(() => {
    if (requestedIndex >= 0) {
      setItemIndex(requestedIndex);
    }
  }, [requestedIndex]);

  // Priority: the requested item (it may be a clip — the player plays whatever path it carries),
  // then the session list at the navigation index, then the first session, then nothing.
  const item: ContentItem | undefined =
    requestedIndex >= 0 ? sessions[requestedIndex] : (requestedItem ?? sessions[itemIndex] ?? sessions[0]);
  // The IPC source is created in an effect, so `source` is null on the first render — the short
  // circuit keeps bookmarks at [] until the source exists (and `item` is undefined then anyway).
  const bookmarks = useMemo(() => (item && source ? source.getBookmarks(item) : []), [source, item]);

  const fallbackDuration =
    item?.endTime !== undefined && item.endTime > 0 ? item.endTime : DEFAULT_SESSION_SECONDS;
  const playback = usePlayback(item?.filePath ?? '', fallbackDuration);
  const { duration, currentTime, seek, playing, videoRef } = playback;

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

  // The clip dialog owns the region list while it is open. When the seam caller supplies its own
  // regions/selection/handler (the read-only region view), those win; otherwise the dialog's
  // regions are used and its selection handler is the loop target.
  const dialog = useClipDialog();
  const regions = externalRegions ?? dialog.regions;
  const selectedRegionId = externalSelectedRegionId ?? dialog.selectedRegionId;
  const onRegionSelect = useCallback(
    (region: TimelineRegion) => {
      if (externalOnRegionSelect) {
        externalOnRegionSelect(region);
        return;
      }
      dialog.selectRegion(dialog.selectedRegionId === region.id ? null : region.id);
    },
    [externalOnRegionSelect, dialog],
  );

  // The dialog wires itself into the IPC surface. The owner of the connection does the sending:
  // `addImportHandler` fires for every CreateClip payload the dialog builds, and `create()` is
  // asynchronous — the backend result arrives later as an `importProgress` message, which is fed
  // back through `applyImportProgress`. The `state` message carries the session's audio-track
  // layout, so the dialog can offer per-track volume/mute when the recording had tracks.
  useEffect(() => {
    return dialog.addImportHandler((content) => {
      client.send('CreateClip', content as Parameters<IpcClient['send']>[1] & { id: string });
    });
  }, [dialog, client]);

  useEffect(() => {
    return client.on('importProgress', (content) => {
      dialog.applyImportProgress(content as Parameters<typeof dialog.applyImportProgress>[0]);
    });
  }, [dialog, client]);

  useEffect(() => {
    return client.on('state', (content) => {
      const message = content as { state?: RecordingState };
      const tracks = message?.state?.audioTracks;
      if (Array.isArray(tracks) && tracks.length > 0) {
        dialog.setAudioTracks(tracks);
      }
    });
  }, [client, dialog]);

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

  const openClipDialog = useCallback(() => {
    if (item) {
      dialog.openDialog(item, currentTime);
    }
  }, [item, currentTime, dialog]);

  // Keyboard: space toggles play/pause, arrows seek (per the navigation spec). The dialog's own
  // inputs are excluded by the input/textarea/button check.
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

  // Segment looping: while the playhead is inside the selected marked segment and playing, when it
  // crosses the segment's end, seek back to the segment's start. The decision is pure
  // (player/clipLoop.ts) and needs the previous sample — the `<video>` element drives currentTime
  // through its own timeupdate events, which are not guaranteed to land exactly on the boundary, and
  // the step-size guard tells a natural play-through from a deliberate seek past the end ("leaving"
  // the segment).
  const lastSampleRef = useRef<number | null>(null);
  useEffect(() => {
    const previousTime = lastSampleRef.current;
    lastSampleRef.current = currentTime;
    if (previousTime === null) {
      return;
    }
    const decision = computeLoopDecision(currentTime, previousTime, playing, regions, selectedRegionId);
    if (decision.shouldLoopBack && decision.region) {
      seek(clampTime(decision.region.start, duration));
    }
  }, [currentTime, playing, regions, selectedRegionId, seek, duration]);

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
        playing={playing}
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
        <button type="button" className="btn" onClick={openClipDialog} aria-label="Open clip dialog">
          Create clip
        </button>
        <span className="muted small">
          Bookmarks: {bookmarks.length}
        </span>
        <span className="muted small">
          Zoom window: {viewWindow.seconds.toFixed(1)}s
        </span>
      </div>

      <ClipDialog client={client} dialog={dialog} />
    </section>
  );
}

export type { BookmarkItem };
