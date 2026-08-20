// SPDX-License-Identifier: GPL-2.0-or-later
//
// The audio page drives the multi-track model:
//
//   - Tracks are destinations, not devices. The user chooses how many audio tracks a recording
//     has. A track is a place in the output file, not bound to a single device.
//   - Sources are routed into tracks. Each track has one or more sources assigned to it, each
//     with its own volume.
//   - Inputs and outputs are both source types. Inputs are mics and other capture devices;
//     outputs are speakers and playback devices — and game audio is an output source.
//   - A track may carry several merged sources, and a source's volume is per-source, never
//     per-track.
//
// The model: a track is `{ id, name, sources: AudioSource[] }`. A source references a device by
// id (a device can be absent while its selection persists) and carries `kind`, `name` and
// `volume`. Device enumeration is a seam for the alpha — the route is built from the devices list
// in the settings message plus a known input/output source set (mic, system, game) so the routing
// UI is real end-to-end. The key deliverable is the routing: add/remove tracks, assign sources to
// tracks, set per-source volume.

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
  { value: 'Normal', label: 'Normal — keep app audio as-is' },
  { value: 'Mute', label: 'Mute — silence the app output while recording' },
  { value: 'Disable', label: 'Disable — no app output at all while recording' },
];

const SOURCE_KIND_LABELS: Record<AudioSourceKind, string> = {
  Input: 'Input',
  Output: 'Output',
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
  for (const device of devices) {
    if (!device?.id || !device?.name) {
      continue;
    }
    const exists = options.some((option) => option.deviceId === device.id || option.id === device.id);
    if (!exists) {
      options.push({
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
  return options;
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
    // source id (mic/system/game). Used to prevent a source being routed twice.
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
  update,
  page,
}: {
  settings: AudioSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
}) {
  const tracks = Array.isArray(settings.tracks) ? settings.tracks : [];
  const devices = Array.isArray(settings.devices) ? settings.devices : [];
  const sourceOptions = buildSourceOptions(devices);

  /**
   * The source options not already routed anywhere — the current track's own sources are
   * excluded too, so a source cannot be assigned twice.
   */
  function addableSources(): SourceOption[] {
    const taken = new Set<string>();
    for (const track of tracks) {
      for (const source of track.sources) {
        taken.add(source.sourceKey ?? source.deviceId ?? source.name);
      }
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
      <Field label="Output mode" hint="How the app's own audio output is handled while recording.">
        <SelectField
          value={settings.outputMode}
          onChange={(value) => update(page, { outputMode: value as AudioOutputMode })}
          options={OUTPUT_MODES}
        />
      </Field>

      <div className="audio-header">
        <h3 className="settings-subheading">
          Tracks <span className="muted small">({tracks.length})</span>
        </h3>
        <Button onClick={addTrack}>Add track</Button>
      </div>

      {tracks.length === 0 && (
        <p className="muted small">No tracks yet — add one to start routing sources into it.</p>
      )}

      {tracks.map((track, trackIndex) => {
        const addable = addableSources();
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
                return (
                  <div className="audio-source" key={sourceIndex}>
                    <div className="audio-source-info">
                      <span className="audio-source-name" title={device?.name ?? source.label ?? source.name}>
                        {device?.name ?? source.label ?? source.name}
                      </span>
                      <span className="pill pill-muted">{SOURCE_KIND_LABELS[source.kind] ?? source.kind}</span>
                      {device && device.name !== (source.label ?? source.name) ? (
                        <span className="muted small">{device.id}</span>
                      ) : null}
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
        A track is a destination in the output file, not a device — one track can carry several
        merged sources, and each source's volume is adjusted independently.
      </p>
    </div>
  );
}

function clampVolume(value: number): number {
  if (!Number.isFinite(value)) {
    return 1;
  }
  return Math.min(1, Math.max(0, value));
}
