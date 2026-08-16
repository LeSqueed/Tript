// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clip dialog controller — the state behind the in-player clip dialog (T9).
//
// The dialog is created in the player (spec/frontend.md — "Clipping — created in the player").
// Opening it proposes a default region centred on the playbar cursor; the user moves, extends or
// shrinks it and marks further regions on the timeline. The two modes differ only in how the
// marked regions are grouped when `CreateClip` is sent:
//
//   - combine  — one CreateClip whose `segments` are all the marked regions (simple concat).
//   - separate — one CreateClip per region, each carrying a single `segments` entry.
//
// `CreateClip` is asynchronous: the backend never returns synchronously, it reports progress as an
// unrelated `importProgress` message (spec/local-ipc.md — "No request/response correlation", so the
// UI tracks in-flight operations by convention). The seam owner (the player) wires the IPC surface
// in: it registers a handler via `addImportHandler` to actually send the payloads `create()` builds,
// and it feeds `importProgress` frames in via `applyImportProgress`.
//
// Regions are in seconds (TimelineRegion.start/end), as are CreateClip's startTime/endTime and
// segments.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ContentItem, CreateClipParameters } from '../../ipc/protocol';
import type { TimelineRegion } from './clipSeam';
import {
  addRegion,
  buildCombineClipPayload,
  buildDefaultRegion,
  buildRegionClipPayload,
  clampTime,
  newClipId,
  newRegionId,
  removeRegion,
} from './clipModel';
import type { ClipMode } from './clipModel';

/** The `importProgress` message content on the wire (spec/local-ipc.md). */
export interface ImportProgressContent {
  status: 'importing' | 'done' | 'error';
  content?: ContentItem;
  error?: string;
}

export type ClipProgressState =
  | { status: 'importing'; clipId: string; label: string }
  | { status: 'done'; clipId: string; label: string }
  | { status: 'error'; clipId: string; label: string; error: string };

export interface ClipDialogController {
  /** The dialog is open and is attached to `session`. */
  open: boolean;
  /** The session the clip is cut from. */
  session: ContentItem | null;
  /** The marked regions, in insertion (display) order. */
  regions: TimelineRegion[];
  /** The region selected on the timeline (segment looping target). */
  selectedRegionId: string | null;
  /** The clipping mode. */
  mode: ClipMode;
  /** The output clip title (defaults to the session title). */
  title: string;
  /** Per-track audio layout from the session's state message, if the recording had tracks. */
  audio: {
    tracks: { id: string; device: string; muted: boolean; volume: number }[];
    volumes: Record<string, number>;
    muted: string[];
  };
  /** The most recent clip result, keyed per clip id. */
  progress: Record<string, ClipProgressState>;
  /** Open the dialog for a session, proposing a default region centred on the playbar cursor. */
  openDialog(session: ContentItem, cursorTime: number): void;
  closeDialog(): void;
  /** Mark a region on the timeline. A fresh id is generated unless `id` is supplied. */
  addRegion(start: number, end: number, id?: string): void;
  removeRegion(id: string): void;
  setMode(mode: ClipMode): void;
  setTitle(title: string): void;
  selectRegion(id: string | null): void;
  setAudioTracks(tracks: { id: string; device: string; muted: boolean; volume: number }[]): void;
  setAudioVolume(trackId: string, volume: number): void;
  toggleAudioMuted(trackId: string): void;
  /** Send the CreateClip command(s) for the current mode. */
  create(): void;
  /** Register a handler invoked with each CreateClip payload `create()` builds. Returns an unsubscribe. */
  addImportHandler(handler: (content: unknown) => void): () => void;
  /** Push an importProgress frame (the backend's async clip result) into the controller. */
  applyImportProgress(content: ImportProgressContent): void;
}

export function useClipDialog(): ClipDialogController {
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

  const openDialog = useCallback((sessionItem: ContentItem, cursorTime: number) => {
    const duration = sessionItem?.endTime ?? 0;
    const region = buildDefaultRegion(cursorTime, duration, newRegionId());
    setSession(sessionItem);
    setRegions([region]);
    setSelectedRegionId(region.id);
    setMode('combine');
    setTitle(sessionItem.title ?? sessionItem.fileName ?? 'Clip');
    setProgress({});
    // Keep any audio tracks the state message already reported (they may arrive before the
    // dialog opens); a fresh `setAudioTracks` from the player replaces them wholesale.
    setAudio((current) => ({ ...current, volumes: {}, muted: [] }));
    setOpen(true);
  }, []);

  const closeDialog = useCallback(() => {
    setOpen(false);
    setSession(null);
    setRegions([]);
    setSelectedRegionId(null);
    setProgress({});
  }, []);

  const handleAddRegion = useCallback((start: number, end: number, id?: string) => {
    setRegions((current) =>
      addRegion(current, {
        id: id ?? newRegionId(),
        start: Math.min(start, end),
        end: Math.max(start, end),
      }),
    );
  }, []);

  const handleRemoveRegion = useCallback((id: string) => {
    setRegions((current) => removeRegion(current, id));
    setSelectedRegionId((selected) => (selected === id ? null : selected));
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

  /** The audio overrides carried into every CreateClip payload, when the session had tracks. */
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
      title: title.trim() || session.title || session.fileName || 'Clip',
      audioTrackVolumes: audioOverrides?.audioTrackVolumes,
      mutedAudioTracks: audioOverrides?.mutedAudioTracks,
    };
    let payloads: CreateClipParameters[];
    if (mode === 'separate') {
      // Each marked region becomes its own clip file: one CreateClip per region.
      payloads = regions.map((region) =>
        buildRegionClipPayload({ ...common, region, id: newClipId(), outputMode: mode }),
      );
    } else {
      // Combine: all marked regions joined into one clip — one CreateClip with all the segments.
      payloads = [buildCombineClipPayload({ ...common, regions, id: newClipId() })];
    }
    for (const payload of payloads) {
      setProgress((current) => ({
        ...current,
        [payload.id]: { status: 'importing', clipId: payload.id, label: payload.title },
      }));
      for (const handler of [...payloadHandlers.current]) {
        try {
          handler(payload);
        } catch {
          // A broken listener must not abort the clip send.
        }
      }
    }
  }, [session, title, mode, regions, audioOverrides]);

  const applyImportProgress = useCallback((content: ImportProgressContent) => {
    if (
      !content ||
      typeof content !== 'object' ||
      (content.status !== 'importing' && content.status !== 'done' && content.status !== 'error')
    ) {
      return;
    }
    setProgress((current) => {
      // The message carries no clip id (spec/local-ipc.md — "No request/response correlation"),
      // so the result is correlated by convention: the most recent in-flight clip. A done/error
      // with nothing in flight is dropped rather than misattributed.
      const inFlightId = Object.keys(current).find((id) => current[id]?.status === 'importing');
      const clipId = inFlightId;
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

  // Drop progress entries once the dialog closes, so a stale `done` from a previous clip never
  // leaks into the next open.
  useEffect(() => {
    if (!open) {
      setProgress({});
    }
  }, [open]);

  const controller = useMemo<ClipDialogController>(
    () => ({
      open,
      session,
      regions,
      selectedRegionId,
      mode,
      title,
      audio,
      progress,
      openDialog,
      closeDialog,
      addRegion: handleAddRegion,
      removeRegion: handleRemoveRegion,
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
      selectedRegionId,
      mode,
      title,
      audio,
      progress,
      openDialog,
      closeDialog,
      handleAddRegion,
      handleRemoveRegion,
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
