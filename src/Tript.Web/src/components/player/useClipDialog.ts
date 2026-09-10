// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ContentItem, CreateClipParameters } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';
import {
  addRegion,
  buildDefaultRegion,
  buildCombineClipPayload,
  buildRegionClipPayload,
  clampTime,
  newClipId,
  newRegionId,
  normalizeRegionBounds,
  MIN_REGION_SECONDS,
  reconcileRegions,
  removeRegion,
} from './clipModel';
import type { ClipMode } from './clipModel';

export interface ImportProgressContent {
  id: string;
  status: 'importing' | 'done' | 'error';
  content?: ContentItem;
  error?: string;
}

export type ClipProgressState =
  | { status: 'importing'; clipId: string; label: string }
  | { status: 'done'; clipId: string; label: string }
  | { status: 'error'; clipId: string; label: string; error: string };

export interface ClipDialogController {
  open: boolean;
  session: ContentItem | null;
  regions: TimelineRegion[];
  duration: number;
  selectedRegionId: string | null;
  mode: ClipMode;
  title: string;
  audio: {
    tracks: { id: string; device: string; muted: boolean; volume: number }[];
    volumes: Record<string, number>;
    muted: string[];
  };
  progress: Record<string, ClipProgressState>;
  attachSession(session: ContentItem | null): void;
  openDialog(session: ContentItem, cursorTime: number): void;
  closeDialog(): void;
  addRegion(start: number, end: number, id?: string): void;
  markRegion(start: number, end: number): void;
  updateRegion(id: string, start: number, end: number): void;
  removeRegion(id: string): void;
  clearRegions(): void;
  setMode(mode: ClipMode): void;
  setTitle(title: string): void;
  selectRegion(id: string | null): void;
  setAudioTracks(tracks: { id: string; device: string; muted: boolean; volume: number }[]): void;
  setAudioVolume(trackId: string, volume: number): void;
  toggleAudioMuted(trackId: string): void;
  create(): void;
  addImportHandler(handler: (content: unknown) => void): () => void;
  applyImportProgress(content: ImportProgressContent): void;
}

export function useClipDialog(clipDuration: number): ClipDialogController {
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState<ContentItem | null>(null);
  const [regions, setRegions] = useState<TimelineRegion[]>([]);
  const [selectedRegionId, setSelectedRegionId] = useState<string | null>(null);
  const [mode, setMode] = useState<ClipMode>('combine');
  const [title, setTitle] = useState('');
  const [audio, setAudio] = useState<{
    tracks: { id: string; device: string; muted: boolean; volume: number }[];
    volumes: Record<string, number>;
    muted: string[];
  }>({ tracks: [], volumes: {}, muted: [] });
  const [progress, setProgress] = useState<Record<string, ClipProgressState>>({});
  const payloadHandlers = useRef(new Set<(content: unknown) => void>());
  const sessionRef = useRef<ContentItem | null>(null);
  const proposalIdRef = useRef<string | null>(null);
  const clipDurationRef = useRef<number>(clipDuration);
  clipDurationRef.current = clipDuration;

  const duration = clipDuration;
  const markDuration = (): number => clipDurationRef.current;

  const attachSession = useCallback((next: ContentItem | null) => {
    if (sessionRef.current?.filePath === next?.filePath) {
      return;
    }
    sessionRef.current = next;
    proposalIdRef.current = null;
    setSession(next);
    setRegions([]);
    setSelectedRegionId(null);
  }, []);

  const openDialog = useCallback(
    (sessionItem: ContentItem, cursorTime: number) => {
      const changed = sessionRef.current?.filePath !== sessionItem.filePath;
      const marked = changed ? [] : regions;
      sessionRef.current = sessionItem;
      setSession(sessionItem);
      if (marked.length > 0) {
        setRegions(marked);
      } else {
        const region = buildDefaultRegion(cursorTime, markDuration(), newRegionId());
        if (region.end - region.start >= MIN_REGION_SECONDS) {
          proposalIdRef.current = region.id;
          setRegions([region]);
          setSelectedRegionId(region.id);
        } else {
          proposalIdRef.current = null;
          setRegions([]);
          setSelectedRegionId(null);
        }
      }
      setMode('combine');
      setTitle(sessionItem.title ?? sessionItem.fileName ?? 'Clip');
      setProgress({});
      setAudio((current) => ({ ...current, volumes: {}, muted: [] }));
      setOpen(true);
    },
     [regions],
  );

  const closeDialog = useCallback(() => {
    setOpen(false);
    setProgress({});
  }, []);

  const handleAddRegion = useCallback((start: number, end: number, id?: string) => {
    const bounds = normalizeRegionBounds(start, end, markDuration());
    if (!bounds) {
      return;
    }
    setRegions((current) => addRegion(current, { id: id ?? newRegionId(), ...bounds }));
  }, []);

  const handleMarkRegion = useCallback((start: number, end: number) => {
    const bounds = normalizeRegionBounds(start, end, markDuration());
    if (!bounds) {
      return;
    }
    const id = newRegionId();
    const proposalId = proposalIdRef.current;
    proposalIdRef.current = null;
    setRegions((current) => {
      const base =
        proposalId && current.some((region) => region.id === proposalId)
          ? removeRegion(current, proposalId)
          : current;
      return addRegion(base, { id, ...bounds });
    });
    setSelectedRegionId(id);
  }, []);

  const handleUpdateRegion = useCallback((id: string, start: number, end: number) => {
    const bounds = normalizeRegionBounds(start, end, markDuration());
    if (!bounds) {
      return;
    }
    setRegions((current) =>
      current.map((region) => (region.id === id ? { ...region, ...bounds } : region)),
    );
    if (proposalIdRef.current === id) {
      proposalIdRef.current = null;
    }
  }, []);

  const handleRemoveRegion = useCallback((id: string) => {
    setRegions((current) => removeRegion(current, id));
    setSelectedRegionId((selected) => (selected === id ? null : selected));
    if (proposalIdRef.current === id) {
      proposalIdRef.current = null;
    }
  }, []);

  const handleClearRegions = useCallback(() => {
    setRegions([]);
    setSelectedRegionId(null);
    proposalIdRef.current = null;
  }, []);

  const handleSelectRegion = useCallback((id: string | null) => {
    setSelectedRegionId(id);
  }, []);

  const handleSetAudioTracks = useCallback(
    (tracks: { id: string; device: string; muted: boolean; volume: number }[]) => {
      setAudio((current) => {
        const volumes = { ...current.volumes };
        const muted = [...current.muted];
        for (const track of tracks) {
          if (!(track.id in volumes)) {
            volumes[track.id] = track.volume;
          }
          if (!track.muted && muted.includes(track.id)) {
            muted.splice(muted.indexOf(track.id), 1);
          }
          if (track.muted && !muted.includes(track.id)) {
            muted.push(track.id);
          }
        }
        return { tracks, volumes, muted };
      });
    },
    [],
  );

  const handleSetAudioVolume = useCallback((trackId: string, volume: number) => {
    setAudio((current) => ({
      ...current,
      volumes: { ...current.volumes, [trackId]: clampTime(volume, 1) },
    }));
  }, []);

  const handleToggleAudioMuted = useCallback((trackId: string) => {
    setAudio((current) => {
      const muted = current.muted.includes(trackId)
        ? current.muted.filter((id) => id !== trackId)
        : [...current.muted, trackId];
      return { ...current, muted };
    });
  }, []);

  const audioOverrides = useMemo(
    () =>
      audio.tracks.length > 0
        ? { audioTrackVolumes: audio.volumes, mutedAudioTracks: audio.muted }
        : undefined,
    [audio.tracks.length, audio.volumes, audio.muted],
  );

  const create = useCallback(() => {
    if (!session || regions.length === 0) {
      return;
    }
    const common = {
      session,
      duration,
      title: title.trim() || session.title || session.fileName || 'Clip',
      audioTrackVolumes: audioOverrides?.audioTrackVolumes,
      mutedAudioTracks: audioOverrides?.mutedAudioTracks,
    };
    let built: (CreateClipParameters | null)[];
    if (mode === 'separate') {
      built = regions.map((region, index) =>
        buildRegionClipPayload({
          ...common,
          title: `${common.title} - ${String(index + 1).padStart(2, '0')}`,
          region,
          id: newClipId(),
          outputMode: mode,
        }),
      );
    } else {
      built = [buildCombineClipPayload({ ...common, regions, id: newClipId() })];
    }
    const payloads = built.filter((payload): payload is CreateClipParameters => payload !== null);
    for (const payload of payloads) {
      setProgress((current) => ({
        ...current,
        [payload.id]: { status: 'importing', clipId: payload.id, label: payload.title },
      }));
      for (const handler of [...payloadHandlers.current]) {
        try {
          handler(payload);
        } catch {
        }
      }
    }
  }, [session, duration, title, mode, regions, audioOverrides]);

  const applyImportProgress = useCallback((content: ImportProgressContent) => {
    if (
      !content ||
      typeof content !== 'object' ||
      (content.status !== 'importing' && content.status !== 'done' && content.status !== 'error')
    ) {
      return;
    }
    setProgress((current) => {
       const clipId = content.id;
       if (!clipId) {
         return current;
       }
      if (!clipId) {
        return current;
      }
      const previous = current[clipId];
      if (!previous) {
        return current;
      }
      if (content.status === 'importing') {
        return current;
      }
      if (content.status === 'done') {
        return { ...current, [clipId]: { status: 'done', clipId, label: previous.label } };
      }
      return {
        ...current,
        [clipId]: { status: 'error', clipId, label: previous.label, error: content.error ?? 'Clip failed' },
      };
    });
  }, []);

  const addImportHandler = useCallback((handler: (content: unknown) => void) => {
    payloadHandlers.current.add(handler);
    return () => {
      payloadHandlers.current.delete(handler);
    };
  }, []);

  useEffect(() => {
    if (!open) {
      setProgress({});
    }
  }, [open]);

  useEffect(() => {
    setRegions((current) => reconcileRegions(current, duration));
  }, [duration]);

  useEffect(() => {
    if (proposalIdRef.current && !regions.some((region) => region.id === proposalIdRef.current)) {
      proposalIdRef.current = null;
    }
    setSelectedRegionId((selected) =>
      selected !== null && !regions.some((region) => region.id === selected) ? null : selected,
    );
  }, [regions]);

  const controller = useMemo<ClipDialogController>(
    () => ({
      open,
      session,
      regions,
      duration,
      selectedRegionId,
      mode,
      title,
      audio,
      progress,
      attachSession,
      openDialog,
      closeDialog,
      addRegion: handleAddRegion,
      markRegion: handleMarkRegion,
      updateRegion: handleUpdateRegion,
      removeRegion: handleRemoveRegion,
      clearRegions: handleClearRegions,
      setMode,
      setTitle,
      selectRegion: handleSelectRegion,
      setAudioTracks: handleSetAudioTracks,
      setAudioVolume: handleSetAudioVolume,
      toggleAudioMuted: handleToggleAudioMuted,
      create,
      addImportHandler,
      applyImportProgress,
    }),
    [
      open,
      session,
      regions,
      duration,
      selectedRegionId,
      mode,
      title,
      audio,
      progress,
      attachSession,
      openDialog,
      closeDialog,
      handleAddRegion,
      handleMarkRegion,
      handleUpdateRegion,
      handleRemoveRegion,
      handleClearRegions,
      handleSelectRegion,
      handleSetAudioTracks,
      handleSetAudioVolume,
      handleToggleAudioMuted,
      create,
      addImportHandler,
      applyImportProgress,
    ],
  );

  return controller;
}
