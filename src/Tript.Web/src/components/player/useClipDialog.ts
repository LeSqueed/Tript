// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clip dialog controller — the state behind the in-player clip dialog (T9).
//
// The dialog is created in the player.
// Opening it proposes a default region centred on the playbar cursor; the user moves, extends or
// shrinks it and marks further regions on the timeline. The two modes differ only in how the
// marked regions are grouped when `CreateClip` is sent:
//
//   - combine  — one CreateClip whose `segments` are all the marked regions (simple concat).
//   - separate — one CreateClip per region, each carrying a single `segments` entry.
//
// `CreateClip` is asynchronous: the backend never returns synchronously, it reports progress as an
// unrelated `importProgress` message. The seam owner (the player) wires the IPC surface in: it
// registers a handler via `addImportHandler` to actually send the payloads `create()` builds, and
// it feeds `importProgress` frames in via `applyImportProgress`.
//
// Regions are in seconds (TimelineRegion.start/end), as are CreateClip's startTime/endTime and
// segments.
//
// Marking happens *outside* the dialog. The dialog is a modal panel over the player, so the playhead
// cannot be moved while it is open; the in/out marking controls therefore live in the player and the
// controller must hold marks before the dialog is ever opened. Two consequences shape the state
// below:
//
//   - `attachSession` — the player attaches the session under review as it plays, so marks can be
//     made (and clamped to the session) with the dialog closed. Attaching a *different* session
//     drops the marks: they belong to the session they were marked on.
//   - the seeded default is a *proposal*, tracked by id. It is only seeded when nothing is marked
//     yet, and the user's first real mark replaces it — nobody wants to clip a region they never
//     asked for. Once a proposal is edited or marked over it stops being a proposal.
//
// Editing (`updateRegion`) replaces a region's bounds in place: same id, same row position, no
// overlap merging. The merge rules in `addRegion` belong to *marking* — applying them to an edit
// would let a drag across a neighbour eat that neighbour irreversibly, mid-gesture, while the
// pointer is still down. An edit is therefore never destructive to other regions.
//
// THE CLIPPABLE DURATION. Every mark, edit and payload is clamped against the duration the caller
// passes in (`useClipDialog(clipDuration)`), which the player resolves from the media itself
// (clipModel's `resolveClipBounds` + `markableDuration`, i.e. only a length the media itself
// reported). It used to be `session.endTime ?? Infinity` — literally unbounded for any recording whose
// content record carries no length, which is the normal case for a recording with no metadata record —
// and then, briefly, the record's declared `endTime`, which is not a measurement of the file either:
// one was seen declaring 100s in front of a 9.13s file. Because marks deliberately outlive the dialog
// (closing it keeps them), a segment marked against a wrong duration could still be sitting there at
// Create time, so the duration is not only a gate on new edits: when it changes, the regions are
// reconciled against it (clipModel's `reconcileRegions` — truncate what straddles the real end, drop
// what lies beyond it), and it is applied once more when the payloads are built.

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

/** The `importProgress` message content on the wire. */
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
  /** The dialog is open and is attached to `session`. */
  open: boolean;
  /** The session the clip is cut from. */
  session: ContentItem | null;
  /** The marked regions, in insertion (display) order. Always inside [0, `duration`]. */
  regions: TimelineRegion[];
  /**
   * The clippable length of the session, seconds — the bound every region is held inside. 0 means the
   * media has not reported its length yet (the content record's declared length does not count: it can
   * overstate the file), in which case no region can be marked or created.
   */
  duration: number;
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
  /**
   * Attach the session under review, so regions can be marked from the player before the dialog is
   * ever opened. Attaching a different session drops the marks made on the previous one.
   */
  attachSession(session: ContentItem | null): void;
  /**
   * Open the dialog for a session. Regions already marked on that session are kept as they are;
   * only when nothing is marked yet is a default region proposed around the playbar cursor.
   */
  openDialog(session: ContentItem, cursorTime: number): void;
  closeDialog(): void;
  /** Mark a region on the timeline. A fresh id is generated unless `id` is supplied. */
  addRegion(start: number, end: number, id?: string): void;
  /**
   * Mark a segment from the player (the in/out points). Bounds are ordered and clamped to the
   * session; a span shorter than MIN_REGION_SECONDS is ignored.
   */
  markRegion(start: number, end: number): void;
  /**
   * Replace a region's bounds — the one seam behind every adjustment (the numeric fields, the
   * set-from-playhead buttons and the timeline drag), so all three produce identical clip bounds.
   * The row keeps its id and its position; bounds are ordered and clamped, and an unusable span
   * (shorter than MIN_REGION_SECONDS) leaves the region alone.
   */
  updateRegion(id: string, start: number, end: number): void;
  removeRegion(id: string): void;
  /** Drop every marked region (the dialog's "Clear all"). */
  clearRegions(): void;
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

/**
 * @param clipDuration The media length regions are clamped against, seconds — the *measured* one, as
 *   the player resolves it from the video element (clipModel's `resolveClipBounds` +
 *   `markableDuration`). 0 means nothing has been measured yet, and nothing is markable until it has.
 *
 *   Required, and deliberately so. This used to be optional, falling back to the attached session's
 *   own declared `endTime` — a number that has been observed claiming 100s for a 9.13s file, i.e. the
 *   very hole `markableDuration` closes on the player's side, reopened one layer down for any caller
 *   that omitted the argument. There is no honest default here: a controller that does not know how
 *   long the media is has to be told, not guess.
 */
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
  // The attached session, mirrored in a ref so marking (which happens with the dialog closed) can
  // clamp to the session duration from a callback with no state dependencies.
  const sessionRef = useRef<ContentItem | null>(null);
  // Legacy dialog-only proposal. The player does not open this dialog without explicit segments;
  // this compatibility path is removed with the dialog during inline clip creation migration.
  const proposalIdRef = useRef<string | null>(null);
  // The id of the *proposed* default region, while it is still untouched. Null once the user has
  // marked, edited or removed it — a proposal only gets replaced silently while it is still a
  // proposal.
  // The caller's duration, mirrored in a ref for the same reason the session is: marking runs from
  // the player's keyboard handler with no state dependencies. Assigned during render rather than in
  // an effect so a mark can never be clamped against the previous render's duration.
  const clipDurationRef = useRef<number>(clipDuration);
  clipDurationRef.current = clipDuration;

  /** The duration marks/edits/payloads are clamped to. 0 when nothing authoritative is known. */
  const duration = clipDuration;
  const markDuration = (): number => clipDurationRef.current;

  const attachSession = useCallback((next: ContentItem | null) => {
    if (sessionRef.current?.filePath === next?.filePath) {
      return;
    }
    // A different recording is under review: its marks are not the previous session's marks.
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
      // Keep any audio tracks the state message already reported (they may arrive before the
      // dialog opens); a fresh `setAudioTracks` from the player replaces them wholesale.
      setAudio((current) => ({ ...current, volumes: {}, muted: [] }));
      setOpen(true);
    },
     [regions],
  );

  // Closing the dialog only closes the panel: the marks stay on the timeline so the user can keep
  // scrubbing, marking and adjusting, then reopen to create. "Clear all" is the explicit discard.
  const closeDialog = useCallback(() => {
    setOpen(false);
    setProgress({});
  }, []);

  // Adding goes through the same gate as marking: this is a public entry point on the controller, so
  // it cannot be the one path that trusts its caller's numbers.
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
      // Both points landed on (almost) the same frame — nothing to clip, so the in point is left
      // standing for the user to try again rather than a sliver of a region being created.
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
    // The freshly marked segment becomes the loop target, so pressing play immediately replays it.
    setSelectedRegionId(id);
  }, []);

  const handleUpdateRegion = useCallback((id: string, start: number, end: number) => {
    const bounds = normalizeRegionBounds(start, end, markDuration());
    if (!bounds) {
      return;
    }
    // In place: same id, same row position, other regions untouched (see the header note on why an
    // edit does not run addRegion's merge rules).
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
      duration,
      title: title.trim() || session.title || session.fileName || 'Clip',
      audioTrackVolumes: audioOverrides?.audioTrackVolumes,
      mutedAudioTracks: audioOverrides?.mutedAudioTracks,
    };
    let built: (CreateClipParameters | null)[];
    if (mode === 'separate') {
      // Each marked region becomes its own clip file: one CreateClip per region.
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
      // Combine: all marked regions joined into one clip — one CreateClip with all the segments.
      built = [buildCombineClipPayload({ ...common, regions, id: newClipId() })];
    }
    // The builders return null for a clip with no in-bounds segment left. Nothing is sent for it: the
    // last thing this controller does before the payload leaves the frontend is check it against the
    // media length, and a payload that fails that check is not repaired, it is dropped.
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
          // A broken listener must not abort the clip send.
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

  // Drop progress entries once the dialog closes, so a stale `done` from a previous clip never
  // leaks into the next open.
  useEffect(() => {
    if (!open) {
      setProgress({});
    }
  }, [open]);

  // Reconcile the marks whenever the clippable duration changes — the regression guard for the
  // provisional-duration hole. A mark made while the player was still going on a placeholder or
  // metadata length must not survive as an out-of-bounds region once the media reports how long it
  // really is: a region straddling the real end is truncated to it, and one lying entirely beyond
  // it is dropped (see clipModel's `reconcileRegions`).
  useEffect(() => {
    setRegions((current) => reconcileRegions(current, duration));
  }, [duration]);

  // A region the reconciliation dropped cannot stay the loop target or proposal.
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
