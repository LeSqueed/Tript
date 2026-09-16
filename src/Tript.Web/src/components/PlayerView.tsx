// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  BookmarkItem,
  ContentItem,
  RecordingState,
  CreateClipParameters,
  TrainingEventDefinition,
  TrainingRegionGroup,
  TrainingSampleMessage,
} from '../ipc/protocol';
import { DEFAULT_SESSION_SECONDS, type SessionSource } from './player/sessionSource';
import { useIpcSessionSource, useSessionSource } from './player/useSessionSource';
import type { TimelineRegion } from './player/clipSeam';
import { clampWindow, zoomWindow, type WindowState } from './player/timelineModel';
import { usePlayback } from './player/usePlayback';
import { FullSessionBar } from './player/FullSessionBar';
import { ZoomedTimeline } from './player/ZoomedTimeline';
import { TransportBar } from './player/TransportBar';
import { PlayerHeader } from './player/PlayerHeader';
import { PlayerSidePanel, availableTabs, type PlayerPanelTab } from './player/PlayerSidePanel';
import { PlaybackSurface } from './player/PlaybackSurface';
import { filterBookmarks } from './player/bookmarks';
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
import { Button, RadioOption } from '../components/ui/controls';
import { TrainingSampleEditor } from './TrainingSampleEditor';

export interface PlayerViewProps {
  client: IpcClient;
  trainingEnabled?: boolean;
  source?: SessionSource | null;
  item?: ContentItem;
  navigationItems?: ContentItem[];
  onItemChange?(item: ContentItem): void;
  onCreateClip?(parameters: CreateClipParameters): void;
  onDelete?(item: ContentItem): void;
  onToggleFavorite?(item: ContentItem): void;
  onReviewSession?(recording: ContentItem): void;
  reviewRecording?: ContentItem;
  highlightCount?: number;
  onBack?(): void;
  regions?: TimelineRegion[];
  selectedRegionId?: string | null;
  onRegionSelect?(region: TimelineRegion): void;
  convertHdrClipsToSdr?: boolean;
  recording?: boolean;
}

export function PlayerView({
  client,
  trainingEnabled = false,
  source: injectedSource,
  item: requestedItem,
  navigationItems,
  onItemChange,
  onCreateClip,
  onDelete,
  onToggleFavorite,
  onReviewSession,
  reviewRecording,
  highlightCount = 0,
  onBack,
  regions: externalRegions,
  selectedRegionId: externalSelectedRegionId,
  onRegionSelect: externalOnRegionSelect,
  convertHdrClipsToSdr = false,
  recording: recordingProp,
}: PlayerViewProps) {
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

  useEffect(() => {
    if (itemIndex >= navigation.length) {
      setItemIndex(0);
    }
  }, [itemIndex, navigation.length]);

  const requestedIndex = useMemo(
    () => (requestedItem ? navigation.findIndex((s) => s.filePath === requestedItem.filePath) : -1),
    [requestedItem, navigation],
  );
  useEffect(() => {
    if (requestedIndex >= 0) {
      setItemIndex(requestedIndex);
    }
  }, [requestedIndex]);

  const item: ContentItem | undefined =
    requestedIndex >= 0 && itemIndex === requestedIndex
      ? navigation[requestedIndex]
      : requestedIndex < 0
        ? (requestedItem ?? navigation[itemIndex] ?? navigation[0])
        : (navigation[itemIndex] ?? navigation[0]);

  const [automaticClips, setAutomaticClips] = useState<RecordingState['automaticClips']>(null);
  const [recordingFromState, setRecordingFromState] = useState(false);
  useEffect(() => client.on('state', (content) => {
    const state = (content as { state?: RecordingState }).state;
    const job = state?.automaticClips;
    setRecordingFromState(state?.recording === true);
    setAutomaticClips(job?.sourceSessionPath === item?.filePath ? job : null);
  }), [client, item?.filePath]);

  const creatingHighlights = item?.contentType === 'recording'
    && (item.automaticClipsProcessing === true || automaticClips?.active === true);
  const highlightsPaused = item?.automaticClipsPaused === true || automaticClips?.paused === true;
  const recording = recordingProp ?? recordingFromState;

  useEffect(() => {
    if (item) {
      onItemChange?.(item);
    }
  }, [item, onItemChange]);
  const bookmarks = useMemo(() => (item && source ? source.getBookmarks(item) : []), [source, item]);
  const [hiddenKinds, setHiddenKinds] = useState<ReadonlySet<string>>(() => new Set());
  const [panelTab, setPanelTab] = useState<PlayerPanelTab>('playlist');
  const awaitingAddedBookmark = useRef(false);
  const visibleBookmarks = useMemo(
    () => filterBookmarks(bookmarks, hiddenKinds),
    [bookmarks, hiddenKinds],
  );

  const toggleBookmarkKind = useCallback((type: string) => {
    setHiddenKinds((previous) => {
      const next = new Set(previous);
      if (!next.delete(type)) {
        next.add(type);
      }
      return next;
    });
  }, []);

  const declaredDuration = item?.endTime !== undefined && item.endTime > 0 ? item.endTime : undefined;
  const fallbackDuration = declaredDuration ?? DEFAULT_SESSION_SECONDS;
  const playback = usePlayback(item?.filePath ?? '', fallbackDuration);
  const { duration, durationKnown, currentTime, seek, playing, videoRef } = playback;
  const [hasStartedPlayback, setHasStartedPlayback] = useState(false);
  const [sdrJobId, setSdrJobId] = useState<string | null>(null);
  const [sdrError, setSdrError] = useState<string | null>(null);

  useEffect(() => client.on('importProgress', (content) => {
    const message = content as { id?: string; status?: string; error?: string };
    if (message.id !== sdrJobId || (message.status !== 'done' && message.status !== 'error'))
      return;
    setSdrJobId(null);
    setSdrError(message.status === 'error' ? message.error ?? 'SDR conversion failed.' : null);
  }), [client, sdrJobId]);

  useEffect(() => {
    setHasStartedPlayback(false);
  }, [item?.filePath]);

  const handleToggleFavorite = useCallback(() => {
    if (item) onToggleFavorite?.(item);
  }, [item, onToggleFavorite]);

  const clipBounds = resolveClipBounds(durationKnown ? duration : undefined, declaredDuration);
  const clipDuration = markableDuration(clipBounds);
  const canMark = clipDuration >= MIN_REGION_SECONDS;

  const [viewWindow, setViewWindow] = useState<WindowState>(() =>
    zoomWindow(0, duration, duration),
  );
  const viewWindowItem = useRef(item?.filePath);
  const viewWindowAdjusted = useRef(false);

  useEffect(() => {
    if (viewWindowItem.current === item?.filePath) {
      return;
    }
    viewWindowItem.current = item?.filePath;
    viewWindowAdjusted.current = false;
    setViewWindow(zoomWindow(0, duration, duration));
  }, [item?.filePath, duration]);

  useEffect(() => {
    setViewWindow((prev) => viewWindowAdjusted.current
      ? zoomWindow(currentTime, prev.seconds, duration)
      : zoomWindow(0, duration, duration));
  }, [currentTime, duration]);

  const setAdjustedViewWindow = useCallback((next: WindowState) => {
    viewWindowAdjusted.current = true;
    setViewWindow(next);
  }, []);

  const dialog = useClipDialog(clipDuration);
  useEffect(() => {
    dialog.attachSession(item ?? null);
  }, [item, dialog.attachSession]);
  const regions = externalRegions ?? dialog.regions;
  const selectedRegionId = externalSelectedRegionId ?? dialog.selectedRegionId;
  const onRegionSelect = useCallback(
    (region: TimelineRegion) => {
      if (externalOnRegionSelect) {
        externalOnRegionSelect(region);
        return;
      }
      const deselecting = dialog.selectedRegionId === region.id;
      dialog.selectRegion(deselecting ? null : region.id);
      if (deselecting) {
        return;
      }
      seek(clampTime(region.start, duration));
    },
    [externalOnRegionSelect, dialog, seek, duration],
  );
  const clipInFlight = Object.values(dialog.progress).some((entry) => entry.status === 'importing');

  useEffect(() => {
    return dialog.addImportHandler((content) => {
      const parameters = content as CreateClipParameters;
      if (onCreateClip) {
        onCreateClip(parameters);
      } else {
        client.send('CreateClip', parameters);
      }
    });
  }, [dialog, client, onCreateClip]);

  useEffect(() => {
    return client.on('importProgress', (content) => {
      const message = content as Parameters<typeof dialog.applyImportProgress>[0];
      dialog.applyImportProgress(message as Parameters<typeof dialog.applyImportProgress>[0]);
    });
  }, [dialog, client]);

  const itemAudioTracks = item?.audioTracks;
  useEffect(() => {
    if (!itemAudioTracks || itemAudioTracks.length === 0) {
      return;
    }
    dialog.setAudioTracks(
      itemAudioTracks.map((track) => ({
        id: String(track.index),
        device: track.name.trim() === '' ? `Track ${track.index + 1}` : track.name,
        muted: false,
        volume: 1,
      })),
    );
  }, [itemAudioTracks, dialog]);

  useEffect(() => {
    return client.on('state', (content) => {
      const message = content as { state?: RecordingState };
      const tracks = message?.state?.audioTracks;
      if (!itemAudioTracks?.length && Array.isArray(tracks) && tracks.length > 0) {
        dialog.setAudioTracks(tracks);
      }
    });
  }, [client, dialog, itemAudioTracks]);

  const navigate = useCallback(
    (delta: number) => {
      if (navigation.length === 0) {
        return;
      }
      playback.prepareItemChange(playing);
      setItemIndex((index) => Math.max(0, Math.min(navigation.length - 1, index + delta)));
    },
    [navigation.length, playback, playing],
  );

  const selectNavigationItem = useCallback((index: number) => {
    if (index === itemIndex || !navigation[index]) return;
    playback.prepareItemChange(playing);
    setItemIndex(index);
  }, [itemIndex, navigation, playback, playing]);

  const captureTrainingFrame = useCallback(() => {
    const video = videoRef.current;
    const gameId = item.gameId ?? item.game;
    if (!trainingEnabled || !gameId || !video || video.videoWidth === 0 || video.videoHeight === 0) {
      return;
    }
    client.send('CaptureTrainingSample', {
      gameId,
      filePath: item.filePath,
      timestampSeconds: currentTime,
      imageWidth: video.videoWidth,
      imageHeight: video.videoHeight,
      labels: [],
    });
  }, [client, currentTime, item, trainingEnabled]);

  const [trainingEvents, setTrainingEvents] = useState<TrainingEventDefinition[]>([]);
  const [trainingRegionGroups, setTrainingRegionGroups] = useState<TrainingRegionGroup[]>([]);
  const [trainingModelAvailable, setTrainingModelAvailable] = useState(false);
  const [labelingSample, setLabelingSample] = useState<TrainingSampleMessage | null>(null);
  const currentGameId = item?.gameId ?? item?.game;

  useEffect(() => {
    setLabelingSample(null);
    setTrainingModelAvailable(false);
    setTrainingRegionGroups([]);
  }, [currentGameId]);

  useEffect(() => {
    if (!trainingEnabled || !currentGameId) return;
    const removeTraining = client.on('training', (content) => {
      const message = (content as { training?: {
        gameId?: string;
        events?: TrainingEventDefinition[];
        regionGroups?: TrainingRegionGroup[];
      } }).training;
      if (message?.events && (!message.gameId || message.gameId === currentGameId)) {
        setTrainingEvents(message.events);
        setTrainingRegionGroups(message.regionGroups ?? []);
      }
      if (message?.gameId === currentGameId) {
        setTrainingModelAvailable(Boolean((message as { model?: unknown }).model));
      }
    });
    const removeSample = client.on('trainingSample', (content) => {
      const message = content as TrainingSampleMessage & { gameId?: string };
      if (!message.gameId || message.gameId === currentGameId) {
        setLabelingSample(message);
      }
    });
    client.send('ListTraining', { gameId: currentGameId });
    return () => {
      removeTraining();
      removeSample();
    };
  }, [client, currentGameId, trainingEnabled]);

  const [volume, setVolume] = useState(1);
  const [muted, setMuted] = useState(false);
  const [playbackRate, setPlaybackRate] = useState(1);
  const lastAudibleVolume = useRef(1);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) {
      return;
    }
    video.volume = volume;
    video.muted = muted;
    video.playbackRate = playbackRate;
    video.defaultPlaybackRate = playbackRate;
  }, [volume, muted, playbackRate, item]);

  const changeVolume = useCallback((next: number) => {
    setVolume(next);
    setMuted(next === 0);
    if (next > 0) {
      lastAudibleVolume.current = next;
    }
  }, []);

  const toggleMute = useCallback(() => {
    if (!muted) {
      setMuted(true);
      return;
    }
    if (volume === 0) {
      setVolume(lastAudibleVolume.current || 1);
    }
    setMuted(false);
  }, [muted, volume]);

  const toggleFullscreen = useCallback(() => {
    const player = playerRootRef.current;
    if (!player || !document.fullscreenEnabled) {
      return;
    }
    if (document.fullscreenElement === player) {
      void document.exitFullscreen();
    } else {
      void player.requestFullscreen();
    }
  }, []);

  const openClipDialog = useCallback(() => {
    if (item && regions.length > 0) {
      dialog.openDialog(item, currentTime);
    }
  }, [item, currentTime, dialog, regions.length]);

  const [markInTime, setMarkInTime] = useState<number | null>(null);
  const canAdjustRegions = externalRegions === undefined;
  const playerRootRef = useRef<HTMLElement>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);

  useEffect(() => {
    const onFullscreenChange = () => setIsFullscreen(document.fullscreenElement === playerRootRef.current);
    document.addEventListener('fullscreenchange', onFullscreenChange);
    return () => document.removeEventListener('fullscreenchange', onFullscreenChange);
  }, []);

  useEffect(() => {
    setMarkInTime(null);
    setHiddenKinds(new Set());
    awaitingAddedBookmark.current = false;
  }, [item?.filePath]);

  useEffect(() => {
    if (awaitingAddedBookmark.current && bookmarks.length > 0) {
      awaitingAddedBookmark.current = false;
      setPanelTab('bookmarks');
    }
  }, [bookmarks]);

  const canBookmark = item?.contentType === 'recording';

  const addBookmark = useCallback(() => {
    if (!item || item.contentType !== 'recording') {
      return;
    }
    awaitingAddedBookmark.current = true;
    client.send('AddBookmark', {
      contentType: 'recording',
      filePath: item.filePath,
      id: '',
      time: currentTime,
      type: 'manual',
    });
  }, [client, item, currentTime]);

  const deleteBookmark = useCallback(
    (bookmark: BookmarkItem) => {
      if (!item || item.contentType !== 'recording') {
        return;
      }
      client.send('DeleteBookmark', {
        contentType: 'recording',
        filePath: item.filePath,
        id: bookmark.id,
      });
    },
    [client, item],
  );

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
    if (Math.abs(out - markInTime) >= MIN_REGION_SECONDS) {
      setMarkInTime(null);
    }
  }, [markInTime, currentTime, clipDuration, dialog]);

  const markSegmentAtPlayhead = useCallback(() => {
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

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent): void {
      const eventTarget = event.target instanceof HTMLElement ? event.target : null;
      const activeElement = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      const target = eventTarget && eventTarget !== document.body ? eventTarget : activeElement;
      if (document.querySelector('[role="dialog"]')
        || target?.closest('button, a[href], input, textarea, select, [contenteditable="true"], [role="slider"], [role="radio"]')) {
        return;
      }
      if (event.ctrlKey || event.metaKey || event.altKey) {
        return;
      }
      const key = event.key.toLowerCase();
      if (event.repeat && event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
        return;
      }
      if (event.key === 'Escape') {
        event.preventDefault();
        if (markInTime !== null) {
          setMarkInTime(null);
        } else {
          onBack?.();
        }
        return;
      }
      if (key === 'delete' && item && onDelete) {
        event.preventDefault();
        onDelete(item);
        return;
      }
      if (key === 'f' && onToggleFavorite) {
        event.preventDefault();
        handleToggleFavorite();
        return;
      }
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
      if (key === 'b') {
        event.preventDefault();
        addBookmark();
        return;
      }
      if (event.code === 'Space') {
        event.preventDefault();
        playback.togglePlayPause();
      } else if (event.shiftKey && event.key === 'ArrowLeft') {
        event.preventDefault();
        navigate(-1);
      } else if (event.shiftKey && event.key === 'ArrowRight') {
        event.preventDefault();
        navigate(1);
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
  }, [playback, currentTime, seek, markIn, markOut, markSegmentAtPlayhead, addBookmark, onToggleFavorite, handleToggleFavorite, item, onDelete, navigate, markInTime, onBack]);

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
      seek(clampTime(target, duration));
    }
  }, [selectedRegion, currentTime, clipDuration, duration, seek]);

  if (!item) {
    return (
      <section className="player-view">
        <p className="muted">No recordings.</p>
      </section>
    );
  }

  const handleAutomaticClips = () => {
    if (creatingHighlights) {
      client.send('PauseAutomaticClips');
    } else {
      client.send('CreateAutomaticClips', { filePath: item.filePath });
    }
  };

  const handleConvertToSdr = () => {
    if (recording || !convertHdrClipsToSdr || item.isHdr !== true || sdrJobId
      || (item.contentType !== 'clip' && item.contentType !== 'highlight'))
      return;
    const id = `sdr-${Date.now()}-${Math.random().toString(36).slice(2)}`;
    setSdrError(null);
    setSdrJobId(id);
    client.send('ConvertToSdr', { id, contentType: item.contentType, filePath: item.filePath });
  };

  const handleRename = (renamedItem: ContentItem, title: string) => {
    client.send('RenameContent', {
      contentType: renamedItem.contentType,
      fileName: renamedItem.filePath,
      title,
    });
  };

  const handleOpenFileLocation = (locationItem: ContentItem) => {
    client.send('OpenFileLocation', { filePath: locationItem.filePath });
  };

  return (
    <section ref={playerRootRef} className={isFullscreen ? 'player-view player-view-fullscreen' : 'player-view'}>
      <PlayerHeader
        item={item}
        reviewRecording={reviewRecording}
        creatingHighlights={creatingHighlights}
        highlightsPaused={highlightsPaused}
        highlightCount={highlightCount}
        canCreateHighlights={item.hasAutomaticClipCandidates === true}
        onBack={onBack}
        onAutomaticClips={handleAutomaticClips}
        onRename={handleRename}
        onOpenFileLocation={handleOpenFileLocation}
        onReviewSession={onReviewSession}
        convertHdrClipsToSdr={convertHdrClipsToSdr}
        recording={recording}
        convertingToSdr={sdrJobId !== null}
        onConvertToSdr={handleConvertToSdr}
        conversionError={sdrError}
      />
      <PlaybackSurface
        item={item}
        videoRef={videoRef}
        playing={playing}
        hasStartedPlayback={hasStartedPlayback}
        onTogglePlayPause={playback.togglePlayPause}
        onTimeUpdate={playback.onVideoTimeUpdate}
        onDurationChange={playback.onVideoDuration}
        onPlay={() => {
          setHasStartedPlayback(true);
          playback.onVideoTimeUpdate(videoRef.current?.currentTime ?? 0);
          playback.onVideoPlay();
        }}
        onPause={() => {
          playback.onVideoTimeUpdate(videoRef.current?.currentTime ?? 0);
          playback.onVideoPause();
        }}
        onEnded={playback.onVideoEnded}
        onError={() => setHasStartedPlayback(false)}
      />

      {}
      <div className="player-console">
        <TransportBar
          playing={playing}
          currentTime={currentTime}
          duration={duration}
          volume={volume}
          muted={muted}
          onTogglePlayPause={playback.togglePlayPause}
          onToggleFullscreen={toggleFullscreen}
          playbackRate={playbackRate}
          onVolumeChange={changeVolume}
          onToggleMute={toggleMute}
          onPlaybackRateChange={setPlaybackRate}
          onPrevious={() => navigate(-1)}
          onNext={() => navigate(1)}
          canNavigatePrevious={itemIndex > 0}
          canNavigateNext={itemIndex < navigation.length - 1}
          itemPosition={navigation.length > 1 ? { current: itemIndex + 1, total: navigation.length } : undefined}
          favorite={item.favorite === true}
          onToggleFavorite={onToggleFavorite ? handleToggleFavorite : undefined}
          onDelete={onDelete ? () => onDelete(item) : undefined}
        />

        <div className="timeline-stack">
        <FullSessionBar
          currentTime={currentTime}
          duration={duration}
          bookmarks={visibleBookmarks}
          window={clampWindow(viewWindow, duration)}
          onSeek={seek}
        />
        {}
        {!isFullscreen && (
          <ZoomedTimeline
            currentTime={currentTime}
            duration={duration}
            window={clampWindow(viewWindow, duration)}
            bookmarks={visibleBookmarks}
            regions={regions}
            selectedRegionId={selectedRegionId}
            markInTime={canAdjustRegions ? markInTime : null}
            onWindowChange={setAdjustedViewWindow}
            onSeek={seek}
            onRegionSelect={onRegionSelect}
            onRegionChange={canAdjustRegions ? updateRegionBounds : undefined}
          />
        )}
        </div>
        <div className="player-clip-tools">
        {(canAdjustRegions || canBookmark) && (
        <div className="player-clip-bar">
          <div className="player-clip-actions">
          {canBookmark && (
            <Button variant="ghost" size="small"
              onClick={addBookmark}
              aria-label="Add a bookmark where you are"
              title="Mark this moment (B)">
              Add bookmark (B)
            </Button>
          )}
          {canAdjustRegions && (
          <>
          <Button variant="primary" size="small"
            onClick={markSegmentAtPlayhead}
            disabled={!canMark}
            aria-label={`Make a ${DEFAULT_REGION_SECONDS}-second clip around where you are`}
            title={`A ${DEFAULT_REGION_SECONDS}s clip around where you are (M)`}>
            Quick clip (M)
          </Button>
          <Button variant="ghost" size="small"

            onClick={markIn}
            disabled={!canMark}
            aria-label="Set the clip start"
            title="Start a clip where you are (I)">
            Set start (I)
          </Button>
          <Button variant="ghost" size="small"

            onClick={markOut}
            disabled={!canMark || markInTime === null}
            aria-label="Set the clip end"
            title="End the clip where you are (O)">
            Set end (O)
          </Button>
          {markInTime !== null && (
            <Button variant="ghost" size="small"

              onClick={() => setMarkInTime(null)}
              aria-label="Clear the clip start"
            >
              Clear start
            </Button>
          )}
          </>
          )}
          </div>
          {trainingEnabled && (item.gameId ?? item.game) && (
            <Button
              variant="ghost"
              size="small"
              onClick={captureTrainingFrame}
              disabled={!durationKnown || !videoRef.current?.videoWidth}
              title="Save this full frame in the training workspace"
            >
              Label frame
            </Button>
          )}
          {canAdjustRegions && (
          <span id="player-clip-hint" className="player-clip-hint muted small" data-testid="player-clip-hint">
            {!canMark
              ?
                'Waiting for the video length. Clips can only be set once the media reports how long it is.'
              : markInTime !== null
              ? `Start at ${formatTime(markInTime)}. Press O (or Set end) where you want the clip to end.`
              : regions.length === 0
                 ? 'Quick clip marks the moment, or press I to set a start, then O to set an end.'
                 : `${regions.length} clip${regions.length === 1 ? '' : 's'} ready. Drag one or its edges on the timeline to adjust.`}
          </span>
          )}
        </div>
        )}

        {regions.length > 0 && <div className="player-footer">
        {}
          <div className="player-clip-mode" role="radiogroup" aria-label="Create as">
            <span className="player-clip-mode-label">Create as</span>
            <RadioOption
              name="player-clip-mode"
              value="combine"
              checked={dialog.mode === 'combine'}
              onChange={() => dialog.setMode('combine')}
              label="One merged clip"
            />
            <RadioOption
              name="player-clip-mode"
              value="separate"
              checked={dialog.mode === 'separate'}
              onChange={() => dialog.setMode('separate')}
              label="Separate clips"
            />
          </div>
        <Button variant="primary" size="small"

          onClick={dialog.create}
          disabled={regions.length === 0 || clipInFlight}
          title={regions.length === 0 ? 'Set at least one clip first' : 'Create clips from the ones you set'}
          aria-describedby="player-clip-hint"
          aria-label="Create clips">
          {clipInFlight ? 'Creating clips…' : 'Create clips'}
        </Button>
        <Button variant="ghost" size="small"

          onClick={openClipDialog}
          disabled={regions.length === 0}
          aria-label="Open clip dialog">
          Adjust details
        </Button>
        </div>
        }
        </div>
      </div>

      {!isFullscreen && availableTabs(navigation.length, bookmarks.length).length > 0 && (
        <PlayerSidePanel
          tab={panelTab}
          onTabChange={setPanelTab}
          items={navigation}
          currentIndex={itemIndex}
          onSelect={selectNavigationItem}
          bookmarks={bookmarks}
          currentTime={currentTime}
          hiddenKinds={hiddenKinds}
          onToggleKind={toggleBookmarkKind}
          onSeek={seek}
          onDeleteBookmark={canBookmark ? deleteBookmark : undefined}
        />
      )}

      <ClipDialog dialog={dialog} currentTime={currentTime} />
      {labelingSample && currentGameId && (
        <TrainingSampleEditor
          client={client}
          gameId={currentGameId}
          sample={labelingSample}
          events={trainingEvents}
          regionGroups={trainingRegionGroups}
          hasModel={trainingModelAvailable}
          onEventsChange={(events, requestId) => client.send('UpdateTrainingEvents', { gameId: currentGameId, requestId, events })}
          onRegionGroupsChange={(regionGroups, requestId) => client.send('UpdateTrainingRegionGroups', { gameId: currentGameId, requestId, regionGroups })}
          onClose={() => setLabelingSample(null)}
        />
      )}
    </section>
  );
}

export type { BookmarkItem };
