// SPDX-License-Identifier: GPL-2.0-or-later
//
// The in-player clip dialog (T9). Created in the player, not in a separate workspace
//.
//
// The dialog owns the region list, the mode selector (combine/separate), the output title, the
// per-track audio overrides (driven by the session's audio-track layout when the recording had
// tracks), and the Create action that sends `CreateClip` — one call for combine (all regions as
// the `segments` of a single clip), one per region for separate. The backend result arrives as an
// `importProgress` message and is rendered in the dialog (in-progress / done / error).
//
// Regions are *marked* in the player (in/out points at the playhead — the dialog is a modal panel,
// so the playhead cannot be moved while it is open) and *adjusted* here as well as by dragging them
// on the timeline. Each row therefore carries three affordances beyond select/remove:
//
//   - typed start/end fields — the precise path, seconds, committed on blur or Enter;
//   - "Start ←" / "End ←" — snap that bound to the playhead, the fast path, and the exact
//     counterpart of the I/O marking keys in the player;
//   - (on the timeline) drag the body to slide the segment, drag an edge to trim it.
//
// All three commit through `dialog.updateRegion`, which normalises and clamps bounds once
// (clipModel.normalizeRegionBounds), so a typed edit, a playhead snap and a dragged edge cannot
// disagree about the region that reaches CreateClip.

import { useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import { formatTime } from './timelineModel';
import type { TimelineRegion } from './clipSeam';
import { DEFAULT_REGION_SECONDS, resizeRegionEnd, resizeRegionStart } from './clipModel';
import type { ClipDialogController, ClipProgressState } from './useClipDialog';
import { Button, Slider } from '../../components/ui/controls';

export interface ClipDialogProps {
  client: IpcClient;
  /** The dialog controller from useClipDialog. */
  dialog: ClipDialogController;
  /**
   * The playhead position, so a region bound can be snapped to it ("Start ←" / "End ←"). The dialog
   * covers the timeline, so the readout on those buttons is the only place the user sees where the
   * playhead is standing.
   */
  currentTime?: number;
}

export function ClipDialog({ dialog, currentTime = 0 }: ClipDialogProps) {
  if (!dialog.open || !dialog.session) {
    return null;
  }
  const entries = Object.entries(dialog.progress).filter(
    (entry): entry is [string, NonNullable<(typeof dialog.progress)[string]>] => entry[1] !== undefined,
  );
  const inFlight = entries.filter(
    (entry): entry is [string, Extract<ClipProgressState, { status: 'importing' }>] =>
      entry[1].status === 'importing',
  );
  const finished = entries.filter(
    (entry): entry is [string, Exclude<ClipProgressState, { status: 'importing' }>] =>
      entry[1].status !== 'importing',
  );

  return (
    <div className="clip-dialog" role="dialog" aria-modal="true" aria-label="Create clip">
      <div className="clip-dialog-panel">
        <div className="clip-dialog-header">
          <h2>Create clip</h2>
          <Button variant="ghost"  onClick={dialog.closeDialog} aria-label="Close clip dialog">
            ×
          </Button>
        </div>

        <div className="clip-field">
          <label className="clip-field-label" htmlFor="clip-output-title">
            Output
          </label>
          <input
            id="clip-output-title"
            className="input"
            value={dialog.title}
            onChange={(event) => dialog.setTitle(event.target.value)}
            placeholder="Clip title"
            spellCheck={false}
          />
          <span className="clip-field-hint">
            {dialog.session.title ?? dialog.session.fileName} ·{' '}
            {dialog.duration > 0 ? formatTime(dialog.duration) : 'length unknown'}
          </span>
        </div>

        <div className="clip-regions">
          <div className="clip-regions-header">
            <span className="subheading">Clips</span>
            <span className="clip-regions-hint">
              {dialog.mode === 'combine'
                ? `${dialog.regions.length} region${dialog.regions.length === 1 ? '' : 's'} → one video`
                : `${dialog.regions.length} region${dialog.regions.length === 1 ? '' : 's'} → ${dialog.regions.length} clip${dialog.regions.length === 1 ? '' : 's'}`}
            </span>
            {dialog.regions.length > 0 && (
              <Button variant="ghost" size="small"
                
                onClick={dialog.clearRegions}
                aria-label="Clear all clips">
                Clear all
              </Button>
            )}
          </div>
          {dialog.regions.length === 0 ? (
            <p className="muted small">
              No clips yet — set one in the player: press I where it should start, then O where it
              should end (or M for a {DEFAULT_REGION_SECONDS}s clip around where you are). Close this
              dialog to reach the timeline; the clips you set stay put.
            </p>
          ) : (
            <p className="muted small">
              Adjust a clip by typing its bounds, snapping them to where you are, or dragging the
              clip (or its edges) on the timeline. More clips: I / O in the player.
            </p>
          )}
          <ul className="clip-region-list">
            {dialog.regions.map((region, index) => (
              <RegionRow
                key={region.id}
                region={region}
                index={index}
                dialog={dialog}
                // The clippable length of the media, not the session's declared `endTime`: the two
                // disagree when the file is shorter than its metadata claims, and the typed fields
                // clamp against the file (see useClipDialog's note on the clippable duration).
                duration={dialog.duration}
                currentTime={currentTime}
              />
            ))}
          </ul>
        </div>

        {dialog.audio.tracks.length > 0 && (
          <div className="clip-audio">
            <span className="subheading">Audio tracks</span>
            <p className="clip-field-hint">
              The recording carried {dialog.audio.tracks.length} track
              {dialog.audio.tracks.length === 1 ? '' : 's'} — adjust per-track volume or mute.
            </p>
            <ul className="clip-audio-list">
              {dialog.audio.tracks.map((track) => {
                const muted = dialog.audio.muted.includes(track.id);
                return (
                  <li key={track.id} className="clip-audio-row">
                    <span className="clip-audio-name" title={track.device}>
                      {track.device}
                    </span>
                    <Slider
                      min={0}
                      max={1}
                      step={0.05}
                      value={muted ? 0 : dialog.audio.volumes[track.id] ?? 1}
                      aria-label={`${track.device} volume`}
                      onChange={(value) => dialog.setAudioVolume(track.id, value)}
                      />
                    <span className="audio-source-volume-value">
                      {muted ? 0 : Math.round((dialog.audio.volumes[track.id] ?? 1) * 100)}%
                    </span>
                    <Button
                      variant="ghost"
                      size="small"
                      active={muted}
                      onClick={() => dialog.toggleAudioMuted(track.id)}
                    >
                      {muted ? 'Unmute' : 'Mute'}
                    </Button>
                  </li>
                );
              })}
            </ul>
          </div>
        )}

        <div className="clip-actions">
          <Button variant="primary"
            
            onClick={dialog.create}
            disabled={dialog.regions.length === 0 || inFlight.length> 0}
          >
            {dialog.mode === 'combine' ? 'Create clip' : `Create ${dialog.regions.length} clip${dialog.regions.length === 1 ? '' : 's'}`}
          </Button>
        </div>

        {(inFlight.length > 0 || finished.length > 0) && (
          <ul className="clip-progress-list">
            {inFlight.map(([id, state]) => (
              <li key={id} className="clip-progress importing" data-testid="clip-progress-importing">
                <span className="clip-progress-label">{state.label}</span>
                <span className="clip-progress-state">Importing…</span>
              </li>
            ))}
            {finished.map(([id, state]) => (
              <li
                key={id}
                className={`clip-progress ${state.status}`}
                data-testid={`clip-progress-${state.status}`}
              >
                <span className="clip-progress-label">{state.label}</span>
                {state.status === 'done' ? (
                  <span className="clip-progress-state">Done</span>
                ) : (
                  <span className="clip-progress-state">{state.error}</span>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function RegionRow({
  region,
  index,
  dialog,
  duration,
  currentTime,
}: {
  region: TimelineRegion;
  index: number;
  dialog: ClipDialogController;
  duration: number;
  currentTime: number;
}) {
  // The typed fields are drafts: a half-typed number ("4" on the way to "42") must not be committed
  // as a bound, so the region only changes on blur or Enter. No draft → the fields mirror the region,
  // which is what keeps them live while the segment is dragged on the timeline.
  const [draft, setDraft] = useState<{ start: string; end: string } | null>(null);
  const startField = draft ? draft.start : secondsField(region.start);
  const endField = draft ? draft.end : secondsField(region.end);

  function commitDraft(): void {
    if (!draft) {
      return;
    }
    setDraft(null);
    const start = Number(draft.start.trim());
    const end = Number(draft.end.trim());
    if (draft.start.trim() === '' || draft.end.trim() === '' || !Number.isFinite(start) || !Number.isFinite(end)) {
      return;
    }
    // Apply the bounds through the same clamping helpers the timeline drag uses: out-of-session
    // values are pulled back to the session, and a bound typed past the opposite one parks against
    // it (MIN_REGION_SECONDS away) instead of inverting the region.
    const withStart = resizeRegionStart(region, start, duration);
    const withEnd = resizeRegionEnd(withStart, end, duration);
    dialog.updateRegion(region.id, withEnd.start, withEnd.end);
  }

  function snapTo(edge: 'start' | 'end'): void {
    const next =
      edge === 'start'
        ? resizeRegionStart(region, currentTime, duration)
        : resizeRegionEnd(region, currentTime, duration);
    dialog.updateRegion(region.id, next.start, next.end);
  }

  return (
    <li className={`clip-region-row ${dialog.selectedRegionId === region.id ? 'selected' : ''}`}>
      <div className="clip-region-main">
        <Button
          type="button"
          className="clip-region-select"
          onClick={() => dialog.selectRegion(dialog.selectedRegionId === region.id ? null : region.id)}
          aria-label={dialog.selectedRegionId === region.id ? `Deselect clip ${index + 1}` : `Select clip ${index + 1}`}
          title="Select on the timeline to loop it while you are inside it"
        >
          <span className="clip-region-index">{index + 1}</span>
          <span className="clip-region-times">
            {formatTime(region.start)} – {formatTime(region.end)}
          </span>
          <span className="clip-region-length">
            {formatDuration(region.end - region.start)}
          </span>
        </Button>
        <Button variant="ghost" size="small"
          
          onClick={() => dialog.removeRegion(region.id)}
          aria-label={`Remove region ${index + 1}`}
        >
          Remove
        </Button>
      </div>
      <div className="clip-region-edit">
        <label className="clip-region-time">
          <span className="clip-region-time-label">Start</span>
          <input
            type="number"
            className="input clip-region-time-input"
            min={0}
            max={Math.max(0, duration)}
            step={0.1}
            value={startField}
            aria-label={`Clip ${index + 1} start, seconds`}
            onChange={(event) => setDraft({ start: event.target.value, end: endField })}
            onBlur={commitDraft}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                commitDraft();
              }
            }}
          />
        </label>
        <Button variant="ghost" size="small"
          
          onClick={() => snapTo('start')}
          aria-label={`Start clip ${index + 1} where you are`}
          title={`Start it where you are (${formatTime(currentTime)})`}
        >
          Start ← {formatTime(currentTime)}
        </Button>
        <label className="clip-region-time">
          <span className="clip-region-time-label">End</span>
          <input
            type="number"
            className="input clip-region-time-input"
            min={0}
            max={Math.max(0, duration)}
            step={0.1}
            value={endField}
            aria-label={`Clip ${index + 1} end, seconds`}
            onChange={(event) => setDraft({ start: startField, end: event.target.value })}
            onBlur={commitDraft}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                commitDraft();
              }
            }}
          />
        </label>
        <Button variant="ghost" size="small"
          
          onClick={() => snapTo('end')}
          aria-label={`End clip ${index + 1} where you are`}
          title={`End it where you are (${formatTime(currentTime)})`}
        >
          End ← {formatTime(currentTime)}
        </Button>
      </div>
    </li>
  );
}

/** A bound as the numeric field shows it: seconds, at most two decimals, no trailing zeroes. */
function secondsField(seconds: number): string {
  return String(Math.round(Math.max(0, seconds) * 100) / 100);
}

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) {
    return `${s}s`;
  }
  return formatTime(s);
}
