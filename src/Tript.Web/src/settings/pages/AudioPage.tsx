// SPDX-License-Identifier: GPL-2.0-or-later
//

import type { SettingsPageName } from '../useSettings';
import type {
  AudioDeviceSetting,
  AudioOutputMode,
  AudioSettings,
  AudioSource,
  AudioSourceKind,
  AudioTrack,
} from '../settingsModel';
import { Button, Field, SelectField, Slider, TextField } from '../../components/ui/controls';

const OUTPUT_MODES: { value: AudioOutputMode; label: string }[] = [
  { value: 'Normal', label: 'Normal (hear it as usual)' },
  { value: 'Mute', label: 'Mute (silent while recording)' },
  { value: 'Disable', label: 'Disable (off entirely while recording)' },
];

const SOURCE_KIND_LABELS: Record<AudioSourceKind, string> = {
  Input: 'Mic',
  Output: 'Playback',
};

const SOURCE_KIND_TITLES: Record<AudioSourceKind, string> = {
  Input: 'WASAPI capture endpoint (input)',
  Output: 'WASAPI render endpoint (output)',
};

interface SourceOption {
  id: string;
  label: string;
  kind: AudioSourceKind;
  deviceId?: string;
}

/** Build the known source set: mic, system output, game audio — plus every known device. */
export function buildSourceOptions(devices: AudioDeviceSetting[]): SourceOption[] {
  const options: SourceOption[] = [
    { id: 'mic', label: 'Microphone', kind: 'Input' },
    { id: 'system', label: 'System output', kind: 'Output' },
    { id: 'game', label: 'Game audio', kind: 'Output' },
  ];
  const deviceOptions: SourceOption[] = [];
  for (const device of devices) {
    if (!device?.id || !device?.name) {
      continue;
    }
    const exists = deviceOptions.some((option) => option.deviceId === device.id || option.id === device.id);
    if (!exists) {
      deviceOptions.push({
        id: device.id,
        label: device.name,
        // A device's direction decides the capture type it routes to: an output endpoint is a
        // render device (speaker/headset) captured by wasapi_output_capture on that device, not
        // an input capture. Absent direction (an older backend) stays an Input.
        kind: device.direction === 'Output' ? 'Output' : 'Input',
        deviceId: device.id,
      });
    }
  }
  deviceOptions.sort((left, right) =>
    left.label.localeCompare(right.label, undefined, { sensitivity: 'base' })
      || left.id.localeCompare(right.id),
  );
  return [...options, ...deviceOptions];
}

/** The device behind a source, if any — resolved by id against the device list. */
function deviceForSource(source: AudioSource, devices: AudioDeviceSetting[]): AudioDeviceSetting | null {
  if (source.deviceId) {
    const device = devices.find((d) => d.id === source.deviceId);
    if (device) {
      return device;
    }
  }
  return null;
}

function makeSource(option: SourceOption): AudioSource {
  const source: AudioSource = {
    name: option.label,
    kind: option.kind,
    label: option.label,
    volume: 1,
    // The stable routing key: the device id when this is a device selection, else the built-in
    // source id (mic/system/game). Used to prevent a source being routed twice in one track.
    sourceKey: option.deviceId ?? option.id,
  };
  if (option.deviceId) {
    source.deviceId = option.deviceId;
  }
  return source;
}

function makeTrack(index: number): AudioTrack {
  return { id: crypto.randomUUID(), name: `Track ${index}`, sources: [] };
}

export function AudioPage({
  settings,
  levels,
  update,
  page,
}: {
  settings: AudioSettings;
  levels?: Readonly<Record<string, number>>;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
}) {
  const tracks = Array.isArray(settings.tracks) ? settings.tracks : [];
  const devices = Array.isArray(settings.devices) ? settings.devices : [];
  const sourceOptions = buildSourceOptions(devices);

  function addableSources(track: AudioTrack): SourceOption[] {
    const taken = new Set<string>();
    for (const source of track.sources) {
      taken.add(sourceIdentity(source));
    }
    return sourceOptions.filter((option) => {
      const key = option.deviceId ?? option.id;
      return !taken.has(key);
    });
  }

  function addTrack() {
    const next = [...tracks, makeTrack(tracks.length + 1)];
    update(page, { tracks: next });
  }

  function removeTrack(index: number) {
    if (tracks.length <= 1) {
      return;
    }
    const next = tracks.filter((_, i) => i !== index);
    update(page, { tracks: next });
  }

  function renameTrack(index: number, name: string) {
    const next = tracks.map((track, i) => (i === index ? { ...track, name } : track));
    update(page, { tracks: next });
  }

  function addSource(trackIndex: number, option: SourceOption) {
    const source = makeSource(option);
    const next = tracks.map((track, i) =>
      i === trackIndex ? { ...track, sources: [...track.sources, source] } : track,
    );
    update(page, { tracks: next });
  }

  function removeSource(trackIndex: number, sourceIndex: number) {
    const next = tracks.map((track, i) =>
      i === trackIndex
        ? { ...track, sources: track.sources.filter((_, si) => si !== sourceIndex) }
        : track,
    );
    update(page, { tracks: next });
  }

  function setSourceVolume(trackIndex: number, sourceIndex: number, volume: number) {
    const next = tracks.map((track, i) =>
      i === trackIndex
        ? {
            ...track,
            sources: track.sources.map((source, si) =>
              si === sourceIndex ? { ...source, volume: clampVolume(volume) } : source,
            ),
          }
        : track,
    );
    update(page, { tracks: next });
  }

  return (
    <div className="settings-page" data-page="audio">
      <p className="settings-page-note">
        With no custom tracks, Tript records the audio in OBS's programme mix. Add tracks below to
        choose and adjust specific microphones or playback sources.
      </p>

      <div className="audio-header">
        <h3 className="subheading">
          Tracks <span className="muted small">({tracks.length})</span>
        </h3>
        <Button onClick={addTrack}>Add track</Button>
      </div>

      {tracks.length === 0 && (
        <p className="muted small">No tracks yet. Add one to start routing sources into it.</p>
      )}

      {tracks.map((track, trackIndex) => {
        const addable = addableSources(track);
        return (
          <div className="audio-track" key={track.id}>
            <div className="audio-track-header">
              <TextField
                value={track.name}
                onChange={(value) => renameTrack(trackIndex, value)}
              />
              <Button variant="danger"
                onClick={() => removeTrack(trackIndex)}
                disabled={tracks.length <= 1}
                title={tracks.length <= 1 ? 'A recording needs at least one track' : 'Remove this track'}
              >
                Remove
              </Button>
            </div>

            <div className="audio-sources">
              {track.sources.length === 0 && (
                <p className="muted small">No sources routed into this track.</p>
              )}
              {track.sources.map((source, sourceIndex) => {
                const device = deviceForSource(source, devices);
                const peak = source.deviceId ? clampLevel(levels?.[source.deviceId] ?? 0) : null;
                const sourceName = device?.name ?? source.label ?? source.name;
                return (
                  <div className="audio-source" key={sourceIndex}>
                    <div className="audio-source-info">
                      <span className="audio-source-name" title={sourceName}>
                        {sourceName}
                      </span>
                      <span className="pill pill-muted" title={SOURCE_KIND_TITLES[source.kind]}>
                        {SOURCE_KIND_LABELS[source.kind] ?? source.kind}
                      </span>
                      {device && device.name !== (source.label ?? source.name) ? (
                        <span className="muted small">{device.id}</span>
                      ) : null}
                    </div>
                    <div className={`audio-source-meter${peak === null ? ' unavailable' : ''}`}>
                      <span className="muted small">Level</span>
                      {peak === null ? (
                        <span className="audio-source-meter-unavailable">Default</span>
                      ) : (
                        <div
                          className="audio-source-meter-track"
                          role="meter"
                          aria-label={`Audio level for ${sourceName}`}
                          aria-valuemin={0}
                          aria-valuemax={100}
                          aria-valuenow={Math.round(peak * 100)}
                        >
                          <span className="audio-source-meter-fill" style={{ transform: `scaleX(${peak})` }} />
                        </div>
                      )}
                    </div>
                    <div className="audio-source-volume">
                      <span className="muted small">Volume</span>
                      <Slider
                        min={0}
                        max={1}
                        step={0.05}
                        value={source.volume}
                        onChange={(value) => setSourceVolume(trackIndex, sourceIndex, value)}
                        aria-label={`Volume for ${source.label ?? source.name}`}
                      />
                      <span className="audio-source-volume-value">{Math.round(source.volume * 100)}%</span>
                    </div>
                    <Button variant="danger"
                      onClick={() => removeSource(trackIndex, sourceIndex)}
                      title={`Remove ${source.label ?? source.name} from this track`}
                    >
                      Remove
                    </Button>
                  </div>
                );
              })}
            </div>

            {addable.length > 0 ? (
              <div className="audio-source-add">
                <Button variant="ghost"
                  onClick={() => addSource(trackIndex, addable[0])}
                  title={`Route ${addable[0].label} into this track`}
                >
                  + Add source
                </Button>
                {addable.length > 1 && (
                  <SelectField
                    aria-label="Source to add to this track"
                    value={addable[0].id}
                    options={addable.map((o) => ({
                      value: o.id,
                      label: `${o.label} (${SOURCE_KIND_LABELS[o.kind]})`,
                    }))}
                    onChange={(selected) => {
                      const option = addable.find((o) => o.id === selected);
                      if (option) {
                        addSource(trackIndex, option);
                      }
                    }}
                  />
                )}
              </div>
            ) : (
              <p className="muted small">Every known source is already routed into a track.</p>
            )}
          </div>
        );
      })}

      <p className="settings-page-note">
        A track is a place in the output file, not a device. One track can carry several merged
        sources, and each source's volume is adjusted independently.
      </p>

      <details className="settings-advanced">
        <summary>Advanced</summary>
        <div className="settings-advanced-body">
          <Field
            label="Audio output while recording"
            hint="What happens to the recorded app's own audio output while Tript records."
          >
            <SelectField
              value={settings.outputMode}
              onChange={(value) => update(page, { outputMode: value as AudioOutputMode })}
              options={OUTPUT_MODES}
            />
          </Field>
        </div>
      </details>
    </div>
  );
}

function sourceIdentity(source: AudioSource): string {
  if (source.deviceId) return source.deviceId;
  if (source.sourceKey) return source.sourceKey;
  if (source.kind === 'Input' && source.name === 'Microphone') return 'mic';
  if (source.kind === 'Output' && source.name === 'System output') return 'system';
  if (source.kind === 'Output' && source.name === 'Game audio') return 'game';
  return `${source.kind}:${source.name}`;
}

function clampVolume(value: number): number {
  if (!Number.isFinite(value)) {
    return 1;
  }
  return Math.min(1, Math.max(0, value));
}

function clampLevel(value: number): number {
  return Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0;
}
