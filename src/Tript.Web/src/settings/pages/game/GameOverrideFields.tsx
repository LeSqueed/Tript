// SPDX-License-Identifier: GPL-2.0-or-later

import type { DisplayCaptureMethod, GameSetting, RecordingMode } from '../../settingsModel';
import { SelectField, TextField } from '../../../components/ui/controls';

const CAPTURE_METHOD_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Global' },
  { value: 'Auto', label: 'Auto' },
  { value: 'Game', label: 'Game capture only' },
  { value: 'Display', label: 'Display capture only' },
];

const RECORDING_MODE_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Inherit global setting' },
  { value: 'Session', label: 'Session' },
  { value: 'SessionWithReplayBuffer', label: 'Session + Replay Buffer' },
  { value: 'ReplayBufferOnly', label: 'Replay buffer only' },
];

const AUTO_RECORD_OVERRIDES: { value: string; label: string }[] = [
  { value: '', label: 'Inherit global setting' },
  { value: 'true', label: 'Always' },
  { value: 'false', label: 'Never' },
];

const isSet = (value: unknown) => value !== null && value !== undefined;

export function hasOverrides(game: GameSetting): boolean {
  if (isSet(game.autoRecordOverride)) return true;
  if (isSet(game.recordingModeOverride?.mode)) return true;
  if (isSet(game.captureMethodOverride?.method)) return true;
  const quality = game.qualityOverride;
  if (quality && [quality.resolutionWidth, quality.resolutionHeight, quality.fps, quality.encoder, quality.quality]
    .some(isSet)) {
    return true;
  }
  const clip = game.automaticClipOverride;
  return Boolean(clip && (isSet(clip.beforeSeconds) || isSet(clip.afterSeconds)));
}

function withoutKey<K extends keyof GameSetting>(game: GameSetting, key: K): GameSetting {
  const { [key]: _dropped, ...rest } = game;
  return rest as GameSetting;
}

export function GameOverrideFields({
  game,
  onPatch,
  onReplace,
  clipBeforeSeconds,
  clipAfterSeconds,
  globalRecordingMode,
  automaticClipsEnabled,
}: {
  game: GameSetting;
  onPatch: (patch: Partial<GameSetting>) => void;
  onReplace: (game: GameSetting) => void;
  clipBeforeSeconds: number;
  clipAfterSeconds: number;
  globalRecordingMode: RecordingMode;
  automaticClipsEnabled: boolean;
}) {
  const effectiveMode = game.recordingModeOverride?.mode ?? globalRecordingMode;
  const automaticClipOverridesDisabled = !automaticClipsEnabled
    || (effectiveMode !== 'SessionWithReplayBuffer' && effectiveMode !== 'ReplayBufferOnly');
  const automaticClipDisabledReason = !automaticClipsEnabled
    ? 'Automatic highlights are off in global settings.'
    : 'Automatic highlights need a replay buffer recording mode.';

  function patchQuality(patch: NonNullable<GameSetting['qualityOverride']>) {
    onPatch({ qualityOverride: { ...(game.qualityOverride ?? {}), ...patch } });
  }

  function patchAutomaticClipOverride(before: number | null, after: number | null) {
    const effectiveBefore = before ?? clipBeforeSeconds;
    const effectiveAfter = after ?? clipAfterSeconds;
    const nextAfter = effectiveAfter < effectiveBefore ? effectiveBefore : after;
    if (before === null && nextAfter === null) {
      onReplace(withoutKey(game, 'automaticClipOverride'));
    } else {
      onPatch({ automaticClipOverride: { beforeSeconds: before, afterSeconds: nextAfter } });
    }
  }

  return (
    <details className="settings-advanced">
      <summary>Customize for this game{hasOverrides(game) ? <span className="settings-advanced-marker">modified</span> : null}</summary>
      <div className="settings-advanced-body game-row-overrides">
        <label className="settings-inline-field">
          <span className="muted small">Recording mode</span>
          <SelectField
            value={game.recordingModeOverride?.mode ?? ''}
            options={RECORDING_MODE_OVERRIDES}
            onChange={(value) => {
              if (value === '') {
                onReplace(withoutKey(game, 'recordingModeOverride'));
              } else {
                onPatch({ recordingModeOverride: { mode: value as RecordingMode } });
              }
            }}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">Auto-record on launch</span>
          <SelectField
            value={game.autoRecordOverride === true ? 'true' : game.autoRecordOverride === false ? 'false' : ''}
            options={AUTO_RECORD_OVERRIDES}
            onChange={(value) => {
              onPatch({ autoRecordOverride: value === '' ? null : value === 'true' });
            }}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">Capture</span>
          <SelectField
            value={game.captureMethodOverride?.method ?? ''}
            options={CAPTURE_METHOD_OVERRIDES}
            onChange={(value) => {
              if (value === '') {
                onReplace(withoutKey(game, 'captureMethodOverride'));
              } else {
                onPatch({ captureMethodOverride: { method: value as DisplayCaptureMethod } });
              }
            }}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">FPS</span>
          <TextField
            type="number"
            min={1}
            value={game.qualityOverride?.fps ?? ''}
            placeholder="global"
            onChange={(value) => patchQuality({ fps: value === '' ? null : Number(value) })}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">Encoder</span>
          <TextField
            type="text"
            value={game.qualityOverride?.encoder ?? ''}
            placeholder="global"
            onChange={(value) => patchQuality({ encoder: value === '' ? null : value })}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">Quality</span>
          <TextField
            type="number"
            min={1}
            value={game.qualityOverride?.quality ?? ''}
            placeholder="global"
            onChange={(value) => patchQuality({ quality: value === '' ? null : Number(value) })}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">Before (s)</span>
          <TextField
            type="number"
            min={1}
            disabled={automaticClipOverridesDisabled}
            title={automaticClipOverridesDisabled ? automaticClipDisabledReason : undefined}
            value={game.automaticClipOverride?.beforeSeconds ?? ''}
            placeholder="global"
            onChange={(value) => patchAutomaticClipOverride(
              value === '' ? null : Number(value),
              game.automaticClipOverride?.afterSeconds ?? null,
            )}
          />
        </label>

        <label className="settings-inline-field">
          <span className="muted small">After (s)</span>
          <TextField
            type="number"
            min={1}
            disabled={automaticClipOverridesDisabled}
            title={automaticClipOverridesDisabled ? automaticClipDisabledReason : undefined}
            value={game.automaticClipOverride?.afterSeconds ?? ''}
            placeholder="global"
            onChange={(value) => patchAutomaticClipOverride(
              game.automaticClipOverride?.beforeSeconds ?? null,
              value === '' ? null : Number(value),
            )}
          />
        </label>
      </div>
    </details>
  );
}
