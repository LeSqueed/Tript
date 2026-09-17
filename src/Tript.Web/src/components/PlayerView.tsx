// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type {
  BookmarkItem,
  ContentItem,
  RecordingState,
  CreateClipParameters,
} from '../ipc/protocol';
import { DEFAULT_SESSION_SECONDS, type SessionSource } from './player/sessionSource';
import { useIpcSessionSource, useSessionSource } from './player/useSessionSource';
import type { TimelineRegion } from './player/clipSeam';
import { clampWindow } from './player/timelineModel';
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
import {
  clampTime,
  DEFAULT_REGION_SECONDS,
  markableDuration,
  MIN_REGION_SECONDS,
  resolveClipBounds,
} from './player/clipModel';
import { formatTime } from './player/timelineModel';
import { usePlaylistItem } from './player/usePlaylistItem';
import { useHostRecordingState } from './player/useHostRecordingState';
import { useSdrConversion } from './player/useSdrConversion';
import { useTimelineWindow } from './player/useTimelineWindow';
import { usePlayerTraining } from './player/usePlayerTraining';
import { usePlayerVolume } from './player/usePlayerVolume';
import { useFullscreen } from './player/useFullscreen';
import { useClipMarks } from './player/useClipMarks';
import { usePlayerShortcuts } from './player/usePlayerShortcuts';
import { useRegionLooping } from './player/useRegionLooping';
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
  const { item, itemIndex, setItemIndex } = usePlaylistItem(navigation, requestedItem);

  const hostState = useHostRecordingState(client, item?.filePath);
  const creatingHighlights = item?.contentType === 'recording'
    && (item.automaticClipsProcessing === true || hostState.automaticClips?.active === true);
  const highlightsPaused = item?.automaticClipsPaused === true || hostState.automaticClips?.paused === true;
  const recording = recordingProp ?? hostState.recording;

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
  const sdr = useSdrConversion(client);

  useEffect(() => {
    setHasStartedPlayback(false);
  }, [item?.filePath]);

  const handleToggleFavorite = useCallback(() => {
    if (item) onToggleFavorite?.(item);
  }, [item, onToggleFavorite]);

  const clipBounds = resolveClipBounds(durationKnown ? duration : undefined, declaredDuration);
  const clipDuration = markableDuration(clipBounds);
  const canMark = clipDuration >= MIN_REGION_SECONDS;
  const { viewWindow, setAdjustedViewWindow } = useTimelineWindow(item?.filePath, currentTime, duration);

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
      dialog.applyImportProgress(content as Parameters<typeof dialog.applyImportProgress>[0]);
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
    [navigation.length, playback, playing, setItemIndex],
  );

  const selectNavigationItem = useCallback((index: number) => {
    if (index === itemIndex || !navigation[index]) return;
    playback.prepareItemChange(playing);
    setItemIndex(index);
  }, [itemIndex, navigation, playback, playing, setItemIndex]);

  const currentGameId = item?.gameId ?? item?.game;
  const training = usePlayerTraining(client, trainingEnabled, currentGameId);
  const volume = usePlayerVolume(videoRef, item);
  const { rootRef: playerRootRef, isFullscreen, toggleFullscreen } = useFullscreen<HTMLElement>();

  const openClipDialog = useCallback(() => {
    if (item && regions.length > 0) {
      dialog.openDialog(item, currentTime);
    }
  }, [item, currentTime, dialog, regions.length]);

  const canAdjustRegions = externalRegions === undefined;
  const marks = useClipMarks({ dialog, currentTime, clipDuration, canMark, filePath: item?.filePath });
  const { markInTime } = marks;

  useEffect(() => {
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

  const updateRegionBounds = useCallback(
    (id: string, bounds: { start: number; end: number }) => {
      dialog.updateRegion(id, bounds.start, bounds.end);
    },
    [dialog],
  );

  usePlayerShortcuts({
    onEscape: () => {
      if (markInTime !== null) {
        marks.clearMarkIn();
      } else {
        onBack?.();
      }
    },
    onDelete: item && onDelete ? () => onDelete(item) : undefined,
    onFavorite: onToggleFavorite ? handleToggleFavorite : undefined,
    onMarkIn: marks.markIn,
    onMarkOut: marks.markOut,
    onQuickClip: marks.markSegmentAtPlayhead,
    onBookmark: addBookmark,
    onTogglePlay: playback.togglePlayPause,
    onNavigate: navigate,
    onSeekBy: (seconds) => seek(currentTime + seconds),
  });

  useRegionLooping({ currentTime, playing, regions, selectedRegionId, duration, clipDuration, seek });

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
    if (recording || !convertHdrClipsToSdr || item.isHdr !== true || sdr.jobId
      || (item.contentType !== 'clip' && item.contentType !== 'highlight'))
      return;
    sdr.start({ contentType: item.contentType, filePath: item.filePath });
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
        convertingToSdr={sdr.jobId !== null}
        onConvertToSdr={handleConvertToSdr}
        conversionError={sdr.error}
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

      <div className="player-console">
        <TransportBar
          playing={playing}
          currentTime={currentTime}
          duration={duration}
          volume={volume.volume}
          muted={volume.muted}
          onTogglePlayPause={playback.togglePlayPause}
          onToggleFullscreen={toggleFullscreen}
          playbackRate={volume.playbackRate}
          onVolumeChange={volume.changeVolume}
          onToggleMute={volume.toggleMute}
          onPlaybackRateChange={volume.setPlaybackRate}
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
            onClick={marks.markSegmentAtPlayhead}
            disabled={!canMark}
            aria-label={`Make a ${DEFAULT_REGION_SECONDS}-second clip around where you are`}
            title={`A ${DEFAULT_REGION_SECONDS}s clip around where you are (M)`}>
            Quick clip (M)
          </Button>
          <Button variant="ghost" size="small"
            onClick={marks.markIn}
            disabled={!canMark}
            aria-label="Set the clip start"
            title="Start a clip where you are (I)">
            Set start (I)
          </Button>
          <Button variant="ghost" size="small"
            onClick={marks.markOut}
            disabled={!canMark || markInTime === null}
            aria-label="Set the clip end"
            title="End the clip where you are (O)">
            Set end (O)
          </Button>
          {markInTime !== null && (
            <Button variant="ghost" size="small"
              onClick={marks.clearMarkIn}
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
              onClick={() => training.captureFrame(item, currentTime, videoRef.current)}
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
      {training.sample && currentGameId && (
        <TrainingSampleEditor
          client={client}
          gameId={currentGameId}
          sample={training.sample}
          events={training.events}
          regionGroups={training.regionGroups}
          hasModel={training.modelAvailable}
          onEventsChange={(events, requestId) => client.send('UpdateTrainingEvents', { gameId: currentGameId, requestId, events })}
          onRegionGroupsChange={(regionGroups, requestId) => client.send('UpdateTrainingRegionGroups', { gameId: currentGameId, requestId, regionGroups })}
          onClose={training.closeSample}
        />
      )}
    </section>
  );
}

export type { BookmarkItem };
