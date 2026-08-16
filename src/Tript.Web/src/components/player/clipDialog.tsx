// SPDX-License-Identifier: GPL-2.0-or-later
//
// The in-player clip dialog (T9). Created in the player, not in a separate workspace
// (spec/frontend.md — "Clipping — created in the player").
//
// The dialog owns the region list, the mode selector (combine/separate), the output title, the
// per-track audio overrides (driven by the session's audio-track layout when the recording had
// tracks), and the Create action that sends `CreateClip` — one call for combine (all regions as
// the `segments` of a single clip), one per region for separate. The backend result arrives as an
// `importProgress` message and is rendered in the dialog (in-progress / done / error).
//
// Regions are marked on the timeline via the T9 seam — the dialog only lists and edits them.

import type { IpcClient } from '../../ipc/websocketClient';
import { formatTime } from './timelineModel';
import type { TimelineRegion } from './clipSeam';
import type { ClipDialogController, ClipProgressState } from './useClipDialog';

export interface ClipDialogProps {
  client: IpcClient;
  /** The dialog controller from useClipDialog. */
  dialog: ClipDialogController;
}

export function ClipDialog({ dialog }: ClipDialogProps) {
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
          <button type="button" className="btn ghost" onClick={dialog.closeDialog} aria-label="Close clip dialog">
            ×
          </button>
        </div>

        <div className="clip-field">
          <label className="clip-field-label" htmlFor="clip-output-title">
            Output
          </label>
          <input
            id="clip-output-title"
            className="settings-input"
            value={dialog.title}
            onChange={(event) => dialog.setTitle(event.target.value)}
            placeholder="Clip title"
            spellCheck={false}
          />
          <span className="clip-field-hint">
            {dialog.session.title ?? dialog.session.fileName} · {formatTime(dialog.session.endTime ?? 0)}
          </span>
        </div>

        <div className="clip-mode-selector" role="radiogroup" aria-label="Clipping mode">
          <label className={`clip-mode ${dialog.mode === 'combine' ? 'active' : ''}`}>
            <input
              type="radio"
              name="clip-mode"
              value="combine"
              checked={dialog.mode === 'combine'}
              onChange={() => dialog.setMode('combine')}
            />
            <span className="clip-mode-title">Combine</span>
            <span className="clip-mode-note">Regions joined into one video</span>
          </label>
          <label className={`clip-mode ${dialog.mode === 'separate' ? 'active' : ''}`}>
            <input
              type="radio"
              name="clip-mode"
              value="separate"
              checked={dialog.mode === 'separate'}
              onChange={() => dialog.setMode('separate')}
            />
            <span className="clip-mode-title">Separate</span>
            <span className="clip-mode-note">Each region its own clip</span>
          </label>
        </div>

        <div className="clip-regions">
          <div className="clip-regions-header">
            <span className="settings-subheading">Regions</span>
            <span className="clip-regions-hint">
              {dialog.mode === 'combine'
                ? `${dialog.regions.length} region${dialog.regions.length === 1 ? '' : 's'} → one video`
                : `${dialog.regions.length} region${dialog.regions.length === 1 ? '' : 's'} → ${dialog.regions.length} clip${dialog.regions.length === 1 ? '' : 's'}`}
            </span>
          </div>
          {dialog.regions.length === 0 && (
            <p className="muted small">No regions yet. Mark a region on the timeline to add it.</p>
          )}
          <ul className="clip-region-list">
            {dialog.regions.map((region, index) => (
              <RegionRow key={region.id} region={region} index={index} dialog={dialog} />
            ))}
          </ul>
        </div>

        {dialog.audio.tracks.length > 0 && (
          <div className="clip-audio">
            <span className="settings-subheading">Audio tracks</span>
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
                    <input
                      type="range"
                      className="settings-range"
                      min={0}
                      max={1}
                      step={0.05}
                      value={muted ? 0 : dialog.audio.volumes[track.id] ?? 1}
                      aria-label={`${track.device} volume`}
                      onChange={(event) => dialog.setAudioVolume(track.id, Number(event.target.value))}
                    />
                    <span className="audio-source-volume-value">
                      {muted ? 0 : Math.round((dialog.audio.volumes[track.id] ?? 1) * 100)}%
                    </span>
                    <button
                      type="button"
                      className={`btn ghost small ${muted ? 'active' : ''}`}
                      onClick={() => dialog.toggleAudioMuted(track.id)}
                    >
                      {muted ? 'Unmute' : 'Mute'}
                    </button>
                  </li>
                );
              })}
            </ul>
          </div>
        )}

        <div className="clip-actions">
          <button
            type="button"
            className="btn"
            onClick={dialog.create}
            disabled={dialog.regions.length === 0 || inFlight.length > 0}
          >
            {dialog.mode === 'combine' ? 'Create clip' : `Create ${dialog.regions.length} clip${dialog.regions.length === 1 ? '' : 's'}`}
          </button>
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
}: {
  region: TimelineRegion;
  index: number;
  dialog: ClipDialogController;
}) {
  return (
    <li className={`clip-region-row ${dialog.selectedRegionId === region.id ? 'selected' : ''}`}>
      <button
        type="button"
        className="clip-region-select"
        onClick={() => dialog.selectRegion(dialog.selectedRegionId === region.id ? null : region.id)}
        aria-label={dialog.selectedRegionId === region.id ? `Deselect region ${index + 1}` : `Select region ${index + 1}`}
        title="Select on timeline to loop while the playhead is inside it"
      >
        <span className="clip-region-index">{index + 1}</span>
        <span className="clip-region-times">
          {formatTime(region.start)} – {formatTime(region.end)}
        </span>
        <span className="clip-region-length">
          {formatDuration(region.end - region.start)}
        </span>
      </button>
      <button
        type="button"
        className="btn ghost small"
        onClick={() => dialog.removeRegion(region.id)}
        aria-label={`Remove region ${index + 1}`}
      >
        Remove
      </button>
    </li>
  );
}

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) {
    return `${s}s`;
  }
  return formatTime(s);
}
