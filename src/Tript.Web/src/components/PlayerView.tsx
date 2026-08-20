// SPDX-License-Identifier: GPL-2.0-or-later
//
// The player + dual synced timeline. The dual timeline is the heart of the session-review
// experience: a thin full-session bar with bookmark ticks and a zoomed-in precision timeline, both
// driven by the same playhead.

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
import { computeEditSeek, computeLoopDecision } from './player/clipLoop';
import {
  buildDefaultRegion,
  clampTime,
  DEFAULT_REGION_SECONDS,
  markableDuration,
  MIN_REGION_SECONDS,
  newRegionId,
  resolveClipBounds,
} from './player/clipModel';
import { formatTime } from './player/timelineModel';

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
  /** The complete filtered/sorted library result set used for previous/next navigation. */
  navigationItems?: ContentItem[];
  onItemChange?(item: ContentItem): void;
  /**
   * The clip-dialog seam. When a session is playing, the player opens its own dialog that owns the
   * region list; a caller can alternatively supply regions for a read-only region view.
   */
  regions?: TimelineRegion[];
  selectedRegionId?: string | null;
  onRegionSelect?(region: TimelineRegion): void;
}

export function PlayerView({
  client,
  source: injectedSource,
  item: requestedItem,
  navigationItems,
  onItemChange,
  regions: externalRegions,
  selectedRegionId: externalSelectedRegionId,
  onRegionSelect: externalOnRegionSelect,
}: PlayerViewProps) {
  // No injected source → own an IPC-backed one for this view's lifetime. It sends ListContent on
  // creation and re-reads the `content` push, so the list is live.
  const ipcSource = useIpcSessionSource(client, injectedSource === undefined);
  const source = injectedSource ?? ipcSource;
  const { sessions } = useSessionSource(source);
  const navigation = navigationItems ?? sessions;
  const [itemIndex, setItemIndex] = useState(() => {
    const initialIndex = requestedItem
      ? navigation.findIndex((candidate) => candidate.filePath === requestedItem.filePath)
      : -1;
    return initialIndex >= 0 ? initialIndex : 0;
  });

  // Keep the selection in range when the content list changes (a `content` push can remove or
  // reorder sessions): clamp back to the first session, matching the fallback below.
  useEffect(() => {
    if (itemIndex >= navigation.length) {
      setItemIndex(0);
    }
  }, [itemIndex, navigation.length]);

  // The requested item's position in the session list, when it is one of the sessions. Once it is,
  // it becomes the navigation base; navigating away moves within the list as usual.
  const requestedIndex = useMemo(
    () => (requestedItem ? navigation.findIndex((s) => s.filePath === requestedItem.filePath) : -1),
    [requestedItem, navigation],
  );
  useEffect(() => {
    if (requestedIndex >= 0) {
      setItemIndex(requestedIndex);
    }
  }, [requestedIndex]);

  // Priority: the requested item (it may be a clip — the player plays whatever path it carries),
  // then the session list at the navigation index, then the first session, then nothing.
  const item: ContentItem | undefined =
    requestedIndex >= 0 && itemIndex === requestedIndex
      ? navigation[requestedIndex]
      : requestedIndex < 0
        ? (requestedItem ?? navigation[itemIndex] ?? navigation[0])
        : (navigation[itemIndex] ?? navigation[0]);

  useEffect(() => {
    if (item) {
      onItemChange?.(item);
    }
  }, [item, onItemChange]);
  // The IPC source is created in an effect, so `source` is null on the first render — the short
  // circuit keeps bookmarks at [] until the source exists (and `item` is undefined then anyway).
  const bookmarks = useMemo(() => (item && source ? source.getBookmarks(item) : []), [source, item]);

  // The session's declared length, when the content record carries one. Neither this nor
  // `DEFAULT_SESSION_SECONDS` is a length: the constant is a placeholder that keeps the timeline
  // drawable for a recording with no metadata record (the normal case for one that was never
  // post-processed), and the declared length is a second-hand number that has been seen claiming 100s
  // for a 9.13s file. Both are good enough to lay out a timeline and useless as a bound — see below.
  const declaredDuration = item?.endTime !== undefined && item.endTime > 0 ? item.endTime : undefined;
  const fallbackDuration = declaredDuration ?? DEFAULT_SESSION_SECONDS;
  const playback = usePlayback(item?.filePath ?? '', fallbackDuration);
  const { duration, durationKnown, currentTime, seek, playing, videoRef } = playback;

  // The bound every marked segment lives inside — the media's own duration, and nothing else.
  // MEASURED BUG (this is the hole this resolution closes): `duration` above starts at
  // `fallbackDuration`, so on a session with no `endTime` the player believed the video was 120s
  // long until metadata arrived.
  const clipBounds = resolveClipBounds(durationKnown ? duration : undefined, declaredDuration);
  const clipDuration = markableDuration(clipBounds);
  // Nothing can be marked inside a media whose length nobody has measured, or one too short to hold
  // the shortest allowed segment. The mark controls say so rather than silently doing nothing.
  const canMark = clipDuration >= MIN_REGION_SECONDS;

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
  const dialog = useClipDialog(clipDuration);
  // Attach the session under review so segments can be marked before the dialog is ever opened
  // (and so switching sessions drops the previous session's marks).
  useEffect(() => {
    dialog.attachSession(item ?? null);
  }, [item, dialog.attachSession]);
  const regions = externalRegions ?? dialog.regions;
  const selectedRegionId = externalSelectedRegionId ?? dialog.selectedRegionId;
  const onRegionSelect = useCallback(
    (region: TimelineRegion) => {
      if (externalOnRegionSelect) {
        // The caller owns the selection (the read-only region view): it decides what a click means,
        // so the player must not move the playhead behind its back.
        externalOnRegionSelect(region);
        return;
      }
      // The click toggles: clicking the looping segment again turns the loop off. Deselecting is not
      // "review this segment", so it leaves the playhead exactly where the user left it.
      const deselecting = dialog.selectedRegionId === region.id;
      dialog.selectRegion(deselecting ? null : region.id);
      if (deselecting) {
        return;
      }
      // Selecting a segment starts its loop at the top — the playhead moves to the segment's first
      // frame rather than the loop engaging only if playback happened to already be inside it. The
      // seek happens whether or not the video is playing (paused, it shows the segment's first
      // frame, which is what "move to the start" means on a still), but selection deliberately does
      // NOT start playback the user did not ask for.
      seek(clampTime(region.start, duration));
    },
    [externalOnRegionSelect, dialog, seek, duration],
  );
  const clipInFlight = Object.values(dialog.progress).some((entry) => entry.status === 'importing');

  // The dialog wires itself into the IPC surface. The owner of the connection does the sending:
  // `addImportHandler` fires for every CreateClip payload the dialog builds, and `create()` is
  // asynchronous — the backend result arrives later as an `importProgress` message, which is fed
  // back through `applyImportProgress`.
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
      if (navigation.length === 0) {
        return;
      }
      setItemIndex((index) => (index + delta + navigation.length) % navigation.length);
    },
    [navigation.length],
  );

  // Volume lives here rather than in the transport row so it survives moving between sessions: the
  // video element is remounted per source, the preference is not. Muted keeps the level rather than
  // zeroing it, so unmuting returns to where the user left it.
  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);
  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }
    video.volume = volume;
    video.muted = muted;
  }, [volume, muted, item]);

  const toggleFullscreen = useCallback(() => {
    client.send('ToggleFullscreen', { enabled: true });
  }, [client]);

  const openClipDialog = useCallback(() => {
    if (item && regions.length > 0) {
      dialog.openDialog(item, currentTime);
    }
  }, [item, currentTime, dialog, regions.length]);

  // The pending in point: set at the playhead by I, closed into a segment by O. It lives here rather
  // than in the controller because it is a transport gesture, not part of the clip — nothing is
  // marked until the out point lands.
  const [markInTime, setMarkInTime] = useState<number | null>(null);
  const canAdjustRegions = externalRegions === undefined;

  useEffect(() => {
    setMarkInTime(null);
  }, [item?.filePath]);

  // The three marking gestures clamp against `clipDuration`, not the timeline's display duration:
  // an in point, an out point and a default-length segment must all land inside the media, and only
  // `clipDuration` knows how long that is. It is 0 while nothing has been measured, which is what
  // refuses all three — the buttons are disabled (`canMark`) and the keyboard path is refused by the
  // model itself (`buildDefaultRegion`/`markRegion` cannot place a region inside a 0s media), so the
  // shortcuts cannot get in behind the disabled buttons.
  const markIn = useCallback(() => {
    if (!canMark) {
      return;
    }
    setMarkInTime(clampTime(currentTime, clipDuration));
  }, [canMark, currentTime, clipDuration]);

  const markOut = useCallback(() => {
    if (markInTime === null) {
      return;
    }
    const out = clampTime(currentTime, clipDuration);
    if (out <= markInTime) {
      return;
    }
    dialog.markRegion(markInTime, out);
    // markRegion refuses a span shorter than MIN_REGION_SECONDS (both points on the same frame).
    // The in point then stays standing, so the user moves the playhead and presses O again rather
    // than discovering that nothing happened and starting over.
    if (Math.abs(out - markInTime) >= MIN_REGION_SECONDS) {
      setMarkInTime(null);
    }
  }, [markInTime, currentTime, clipDuration, dialog]);

  const markSegmentAtPlayhead = useCallback(() => {
    // The same proposal the dialog would have seeded — a full-length segment centred on the playhead,
    // shrunk to the whole media when the media is shorter than the default length.
    const region = buildDefaultRegion(currentTime, clipDuration, newRegionId());
    dialog.markRegion(region.start, region.end);
    setMarkInTime(null);
  }, [currentTime, clipDuration, dialog]);

  const updateRegionBounds = useCallback(
    (id: string, bounds: { start: number; end: number }) => {
      dialog.updateRegion(id, bounds.start, bounds.end);
    },
    [dialog],
  );

  // Keyboard: space toggles play/pause, arrows seek (per the navigation spec), I/O mark a segment's
  // in/out points at the playhead and M marks a default-length one around it. Two levels of
  // suppression.
  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      const target = event.target instanceof HTMLElement ? event.target : null;
      if (target?.closest('input, textarea, select, [contenteditable="true"], [role="slider"]')) {
        return;
      }
      if (event.ctrlKey || event.metaKey || event.altKey) {
        return;
      }
      const onButton = target?.closest('button') != null;
      const key = event.key.toLowerCase();
      if (key === 'i') {
        event.preventDefault();
        markIn();
        return;
      }
      if (key === 'o') {
        event.preventDefault();
        markOut();
        return;
      }
      if (key === 'm') {
        event.preventDefault();
        markSegmentAtPlayhead();
        return;
      }
      if (onButton) {
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
  }, [playback, currentTime, seek, markIn, markOut, markSegmentAtPlayhead]);

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

  // The looping segment while it is being EDITED. The effect above reacts to the playhead moving; this
  // one reacts to the loop's *bounds* moving under a stationary (possibly paused) playhead, which is
  // the other half of the affordance: the loop follows the end as the user drags it, and the playhead
  // comes along when the start is pushed past it (player/clipLoop.ts documents the rule set).
  //
  // The two effects cannot be one. A bound moving looks exactly like a seek from the playhead's point
  // of view — nothing about `currentTime` changed — so feeding edits into `computeLoopDecision` would
  // have it read an edit as "the user left the segment" (or, when the end lands behind the playhead,
  // as a crossing that never happened).
  //
  // Nor do they fight each other, and that is worth spelling out because both of them call `seek`:
  //
  //   - Within one commit React runs effects in declaration order, so the sample effect above runs
  //     first with the *unchanged* playhead (previous === current → no crossing, no decision) and this
  //     one then decides on the edit alone.
  //   - Every seek either effect makes lands exactly on the segment's start, and that position is a
  //     fixed point of the sample path: `crossedEnd` needs currentTime >= end (a start is always at
  //     least MIN_REGION_SECONDS before the end) and `inside` is exclusive of the start, so the
  //     resulting sample decides nothing. A loop-back can never cascade into another one.
  //   - This effect re-baselines its previous bounds on every run, so its own seek (which changes the
  //     playhead, not the bounds) comes back as "no bound moved" — no feedback.
  const selectedRegion = useMemo(
    () => regions.find((region) => region.id === selectedRegionId) ?? null,
    [regions, selectedRegionId],
  );
  const loopBoundsRef = useRef<TimelineRegion | null>(null);
  const loopDurationRef = useRef(clipDuration);
  const reconcilingRef = useRef(false);
  useEffect(() => {
    const previousBounds = loopBoundsRef.current;
    loopBoundsRef.current = selectedRegion;
    // Bounds also change without anyone editing them. When the media reports its real length, the
    // clip controller reconciles every region against it (clipModel's `reconcileRegions` — truncate
    // what straddles the real end, drop what lies beyond it).
    const spendingGrace = reconcilingRef.current;
    if (loopDurationRef.current !== clipDuration) {
      loopDurationRef.current = clipDuration;
      reconcilingRef.current = true;
      return;
    }
    reconcilingRef.current = false;
    if (spendingGrace) {
      return;
    }
    const target = computeEditSeek(previousBounds, selectedRegion, currentTime);
    if (target !== null) {
      // Clamped to the *seekable* duration: the segment lives inside `clipDuration`, but the playhead
      // is a playback position and the video element owns that bound.
      seek(clampTime(target, duration));
    }
  }, [selectedRegion, currentTime, clipDuration, duration, seek]);

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
          disabled={navigation.length <= 1}
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
          disabled={navigation.length <= 1}
          aria-label="Next session"
        >
          Next →
        </button>
      </div>

      <div className="video-frame">
        <video
          ref={videoRef}
          className="video-element"
          // No `controls`: the browser paints those over the picture. The transport row below the
          // video is the control surface. tabIndex keeps the element keyboard-reachable, which
          // `controls` used to provide — the overlay's focus trap matches it by tabindex.
          tabIndex={0}
          src={videoSrc}
          onClick={playback.togglePlayPause}
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
        volume={volume}
        muted={muted}
        onTogglePlayPause={playback.togglePlayPause}
        onToggleFullscreen={toggleFullscreen}
        onVolumeChange={(next) => {
          setVolume(next);
          // Dragging the slider off zero is an unmute in every player anyone has used.
          setMuted(next === 0);
        }}
        onToggleMute={() => setMuted((previous) => !previous)}
      />

      <div className="timeline-stack">
        <FullSessionBar
          currentTime={currentTime}
          duration={duration}
          bookmarks={bookmarks}
          window={clampWindow(viewWindow, duration)}
          onSeek={seek}
        />
        {/*
          The timelines are laid out against the display duration, which is the placeholder length
          while nothing has been measured. That is a layout number, not a bound: a drag on the zoomed
          timeline commits through `updateRegionBounds` → `dialog.updateRegion`, which clamps against
          the clippable duration, so no gesture here can produce a segment outside the media.
        */}
        <ZoomedTimeline
          currentTime={currentTime}
          duration={duration}
          window={clampWindow(viewWindow, duration)}
          bookmarks={bookmarks}
          regions={regions}
          selectedRegionId={selectedRegionId}
          markInTime={canAdjustRegions ? markInTime : null}
          onWindowChange={setViewWindow}
          onSeek={seek}
          onRegionSelect={onRegionSelect}
          onRegionChange={canAdjustRegions ? updateRegionBounds : undefined}
        />
      </div>

      {canAdjustRegions && (
        <div className="player-clip-bar">
          <span className="player-clip-bar-label">Segments</span>
          <button
            type="button"
            className="btn ghost small"
            onClick={markIn}
            disabled={!canMark}
            aria-label="Mark segment in point"
            title="Set the segment's in point at the playhead (I)"
          >
            Mark in (I)
          </button>
          <button
            type="button"
            className="btn ghost small"
            onClick={markOut}
            disabled={!canMark || markInTime === null}
            aria-label="Mark segment out point"
            title="Close the segment at the playhead (O)"
          >
            Mark out (O)
          </button>
          <button
            type="button"
            className="btn ghost small"
            onClick={markSegmentAtPlayhead}
            disabled={!canMark}
            aria-label="Mark segment around the playhead"
            title={`Mark a ${DEFAULT_REGION_SECONDS}s segment around the playhead (M)`}
          >
            Mark {DEFAULT_REGION_SECONDS}s (M)
          </button>
          {markInTime !== null && (
            <button
              type="button"
              className="btn ghost small"
              onClick={() => setMarkInTime(null)}
              aria-label="Clear the in point"
            >
              Clear in
            </button>
          )}
          <span id="player-clip-hint" className="player-clip-hint muted small" data-testid="player-clip-hint">
            {!canMark
              ? // Honest about why the controls are dead: the alternative was to let segments be
                // marked against a length nobody has measured — the placeholder constant, or the
                // content record's declared length, which has been seen overstating the file by 91s.
                // The wait lasts as long as the video element takes to read the file's header.
                'Waiting for the video length — segments can only be marked once the media reports how long it is.'
              : markInTime !== null
              ? `In point at ${formatTime(markInTime)} — press O (or Mark out) at the playhead to close the segment.`
              : regions.length === 0
                ? 'No segments marked yet — press I at the in point, then O at the out point.'
                : `${regions.length} segment${regions.length === 1 ? '' : 's'} marked — drag a segment or its edges on the timeline to adjust, then Create clip.`}
          </span>
        </div>
      )}

      <div className="player-footer">
        <div className="player-clip-mode" role="radiogroup" aria-label="Clip creation mode">
          <span className="player-clip-mode-label">Create as</span>
          <label className={dialog.mode === 'combine' ? 'active' : ''}>
            <input
              type="radio"
              name="player-clip-mode"
              value="combine"
              checked={dialog.mode === 'combine'}
              onChange={() => dialog.setMode('combine')}
            />
            Combine
          </label>
          <label className={dialog.mode === 'separate' ? 'active' : ''}>
            <input
              type="radio"
              name="player-clip-mode"
              value="separate"
              checked={dialog.mode === 'separate'}
              onChange={() => dialog.setMode('separate')}
            />
            Separate
          </label>
        </div>
        <button
          type="button"
          className="btn"
          onClick={dialog.create}
          disabled={regions.length === 0 || clipInFlight}
          title={regions.length === 0 ? 'Mark at least one segment first' : 'Create clips from marked segments'}
          aria-describedby="player-clip-hint"
          aria-label="Create clips"
        >
          {clipInFlight ? 'Creating clips…' : 'Create Clips'}
        </button>
        <button
          type="button"
          className="btn ghost small"
          onClick={openClipDialog}
          disabled={regions.length === 0}
          aria-label="Open clip dialog"
        >
          Adjust details
        </button>
        <span className="muted small">
          Bookmarks: {bookmarks.length}
        </span>
        <span className="muted small">
          Zoom window: {viewWindow.seconds.toFixed(1)}s
        </span>
      </div>

      {Object.keys(dialog.progress).length > 0 && (
        <ul className="player-clip-progress" aria-live="polite">
          {Object.values(dialog.progress).map((entry) => (
            <li key={entry.clipId} className={`player-clip-progress-${entry.status}`}>
              <span>{entry.label}</span>
              <span>
                {entry.status === 'importing'
                  ? 'Creating…'
                  : entry.status === 'done'
                    ? 'Created'
                    : entry.error}
              </span>
            </li>
          ))}
        </ul>
      )}

      <ClipDialog client={client} dialog={dialog} currentTime={currentTime} />
    </section>
  );
}

export type { BookmarkItem };
