// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type {
  BufferSettings,
  DisplayResolution,
  RateControlMode,
  RecordingMode,
  RecordingSettings,
} from '../settingsModel';
import { Field, SelectField, TextField, type SelectOption } from '../../components/ui/controls';
import { ConfirmDialog } from '../../components/ui/confirmDialog';

const RECORDING_MODES: { value: RecordingMode; label: string }[] = [
  { value: 'Session', label: 'Session (one continuous recording)' },
  { value: 'SessionWithReplayBuffer', label: 'Session + replay buffer (records and live highlights)' },
  { value: 'ReplayBufferOnly', label: 'Replay buffer only (highlights without session recordings)' },
];

const QUALITY_OPTIONS: SelectOption[] = [
  { value: '3', label: 'Low' },
  { value: '5', label: 'Medium' },
  { value: '10', label: 'High' },
  { value: '18', label: 'Max' },
];

const FPS_PRESETS = [30, 60, 90, 144];

const RESOLUTION_PRESETS: [number, number][] = [
  [1280, 720],
  [1920, 1080],
  [2560, 1440],
  [3840, 2160],
];

const FALLBACK_ENCODER = 'obs_x264';

const BACKEND_DECIDES_ENCODER = 'x264';

const MIB = 1024 * 1024;

type EncoderFamily = 'x264' | 'vaapi' | 'nvenc' | 'amf' | 'qsv' | 'unknown';

function encoderFamily(id: string): EncoderFamily {
  if (id === FALLBACK_ENCODER) {
    return 'x264';
  }
  const lower = id.toLowerCase();
  if (lower === BACKEND_DECIDES_ENCODER) {
    return 'unknown';
  }
  if (lower.includes('vaapi')) {
    return 'vaapi';
  }
  if (lower.includes('nvenc')) {
    return 'nvenc';
  }
  if (lower.includes('amf')) {
    return 'amf';
  }
  if (lower.includes('qsv')) {
    return 'qsv';
  }
  return 'unknown';
}

const FAMILY_LABELS: Record<EncoderFamily, string> = {
  x264: 'Software (x264)',
  vaapi: 'AMD/Intel (VAAPI)',
  nvenc: 'NVIDIA (NVENC)',
  amf: 'AMD (AMF)',
  qsv: 'Intel (QSV)',
  unknown: '',
};

const RATE_CONTROL_MODES: Record<EncoderFamily, RateControlMode[]> = {
  x264: ['Crf', 'Cbr', 'Vbr'],
  vaapi: ['Cqp', 'Cbr'],
  nvenc: ['Cqp', 'Cbr', 'Vbr'],
  amf: ['Cqp', 'Cbr', 'Vbr'],
  qsv: ['Cqp', 'Cbr', 'Vbr'],
  unknown: ['Cqp', 'Cbr'],
};

const RATE_CONTROL_LABELS: Record<RateControlMode, string> = {
  Crf: 'Constant quality (CRF)',
  Cqp: 'Constant quality (CQP)',
  Cbr: 'Constant bitrate (CBR)',
  Vbr: 'Variable bitrate (VBR)',
};

const QUANTISER_MODES: RateControlMode[] = ['Crf', 'Cqp'];

function withStoredValue(options: SelectOption[], stored: string): SelectOption[] {
  if (!stored || options.some((option) => option.value === stored)) {
    return options;
  }
  return [...options, { value: stored, label: `${stored} (custom)` }];
}

function resolutionValue(width: number, height: number): string {
  return `${width}x${height}`;
}

function parseResolution(value: string): { width: number; height: number } | null {
  const match = /^(\d+)x(\d+)$/.exec(value);
  return match ? { width: Number(match[1]), height: Number(match[2]) } : null;
}

function resolutionOptions(
  storedWidth: number,
  storedHeight: number,
  display?: DisplayResolution,
): SelectOption[] {
  const sizes: [number, number][] = [...RESOLUTION_PRESETS];
  if (display && !sizes.some(([width, height]) => width === display.width && height === display.height)) {
    sizes.push([display.width, display.height]);
  }
  sizes.sort(([aWidth, aHeight], [bWidth, bHeight]) => aWidth * aHeight - bWidth * bHeight);

  const options = sizes.map(([width, height]) => ({
    value: resolutionValue(width, height),
    label:
      display && width === display.width && height === display.height
        ? `${resolutionValue(width, height)} (display)`
        : resolutionValue(width, height),
  }));

  const storedIsSane =
    Number.isInteger(storedWidth) && Number.isInteger(storedHeight) && storedWidth > 0 && storedHeight > 0;
  return storedIsSane ? withStoredValue(options, resolutionValue(storedWidth, storedHeight)) : options;
}

function fpsOptions(stored: number): SelectOption[] {
  const presets = FPS_PRESETS.map((fps) => ({ value: String(fps), label: String(fps) }));
  return Number.isFinite(stored) ? withStoredValue(presets, String(stored)) : presets;
}

function encoderLabel(id: string, ids: string[]): string {
  if (id.toLowerCase() === BACKEND_DECIDES_ENCODER) {
    return 'Automatic (hardware if available)';
  }
  const family = encoderFamily(id);
  const label = FAMILY_LABELS[family];
  if (!label) {
    return id;
  }
  const sameFamily = ids.filter((other) => encoderFamily(other) === family).length;
  return sameFamily > 1 ? `${label} (${id})` : label;
}

function encoderOptions(stored: string, available?: string[]): SelectOption[] {
  const ids = available?.length ? available : [stored, FALLBACK_ENCODER].filter(Boolean);
  const unique = [...new Set(ids)];
  const options = unique.map((id) => ({ value: id, label: encoderLabel(id, unique) }));
  if (!stored || unique.includes(stored)) {
    return options;
  }
  return [...options, { value: stored, label: `${encoderLabel(stored, [stored])} (custom)` }];
}

function rateControlOptions(encoderId: string): SelectOption[] {
  return RATE_CONTROL_MODES[encoderFamily(encoderId)].map((mode) => ({
    value: mode,
    label: RATE_CONTROL_LABELS[mode],
  }));
}

function effectiveRateControl(stored: RateControlMode | undefined, encoderId: string): RateControlMode {
  const modes = RATE_CONTROL_MODES[encoderFamily(encoderId)];
  return stored && modes.includes(stored) ? stored : modes[0];
}

function parseKbps(draft: string): number | null {
  const match = /^\s*(\d+)\s*$/.exec(draft);
  return match ? Number(match[1]) : null;
}

export function RecordingPage({
  settings,
  buffer,
  update,
  page,
  externalPushCount,
  availableEncoders,
  displayResolution,
}: {
  settings: RecordingSettings;
  buffer: BufferSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  externalPushCount: number;
  availableEncoders?: string[];
  displayResolution?: DisplayResolution;
}) {
  const [bitrate, setBitrate] = useState<string>(String(settings.bitrateKbps ?? ''));
  const [maxBitrate, setMaxBitrate] = useState<string>(String(settings.maxBitrateKbps ?? ''));
  const [bufferSizeMiB, setBufferSizeMiB] = useState<string>(
    String(Math.round(buffer.maxSizeBytes / MIB)),
  );
  const [confirmBufferOff, setConfirmBufferOff] = useState(false);

  useEffect(() => {
    setBitrate(String(settings.bitrateKbps ?? ''));
    setMaxBitrate(String(settings.maxBitrateKbps ?? ''));
    setBufferSizeMiB(String(Math.round(buffer.maxSizeBytes / MIB)));
  }, [externalPushCount]);

  function changeMode(value: string) {
    const mode = value as RecordingMode;
    if (mode === 'Session' && settings.automaticClipsEnabled === true) {
      setConfirmBufferOff(true);
      return;
    }
    update(page, { mode });
  }

  function commitResolution(value: string) {
    const parsed = parseResolution(value);
    if (parsed) {
      update(page, { resolutionWidth: parsed.width, resolutionHeight: parsed.height });
    }
  }

  function commitBitrate() {
    const kbps = parseKbps(bitrate);
    if (kbps === null) {
      setBitrate(String(settings.bitrateKbps ?? ''));
    } else {
      setBitrate(String(kbps));
      update(page, { bitrateKbps: kbps });
    }
  }

  function commitMaxBitrate() {
    const kbps = parseKbps(maxBitrate);
    if (kbps === null) {
      setMaxBitrate(String(settings.maxBitrateKbps ?? ''));
    } else {
      setMaxBitrate(String(kbps));
      update(page, { maxBitrateKbps: kbps });
    }
  }

  function commitBufferSize() {
    const parsed = Number(bufferSizeMiB);
    if (Number.isFinite(parsed) && parsed > 0) {
      const bytes = Math.round(parsed * MIB);
      setBufferSizeMiB(String(Math.round(bytes / MIB)));
      update('buffer', { maxSizeBytes: bytes });
    } else {
      setBufferSizeMiB(String(Math.round(buffer.maxSizeBytes / MIB)));
    }
  }

  const encoder = settings.encoder;
  const rateControl = effectiveRateControl(settings.rateControl, encoder);
  const coercedFrom = settings.rateControl && settings.rateControl !== rateControl ? settings.rateControl : undefined;
  const usesQuantiser = QUANTISER_MODES.includes(rateControl);

  return (
    <div className="settings-page" data-page="recording">
      <Field
        label="Recording mode"
        hint="Record full sessions, keep recent footage available for highlights, or do both."
      >
        <SelectField
          value={settings.mode}
          onChange={changeMode}
          options={RECORDING_MODES}
        />
      </Field>

      <Field
        label="Resolution"
        hint={
          displayResolution
            ? "The size of the recorded video. Your display's own size is marked. A stored size outside this list is kept and marked custom."
            : 'The size of the recorded video.'
        }
      >
        <SelectField
          value={resolutionValue(settings.resolutionWidth, settings.resolutionHeight)}
          onChange={commitResolution}
          options={resolutionOptions(settings.resolutionWidth, settings.resolutionHeight, displayResolution)}
        />
      </Field>

      <Field
        label="Frame rate"
        hint="Frames per second. Smoother at higher rates, but larger files and more CPU. A stored rate outside this list is kept and marked custom."
      >
        <SelectField
          value={String(settings.fps)}
          onChange={(value) => update(page, { fps: Number(value) })}
          options={fpsOptions(settings.fps)}
        />
      </Field>

      {usesQuantiser ? (
        <Field
          label="Quality"
          hint="Higher quality looks better and uses more disk space. Applied when a game has no override of its own."
        >
          <SelectField
            value={String(settings.quality)}
            onChange={(value) => update(page, { quality: Number(value) })}
            options={withStoredValue(QUALITY_OPTIONS, String(settings.quality))}
          />
        </Field>
      ) : null}

      <Field
        label="HDR"
        hint="Records in HDR when the captured display is in HDR mode and the encoder supports it. Turn off to always record in SDR, which every player can open. (In SDR, HDR footage is tone-mapped.)"
      >
        <SelectField
          value={settings.enableHdr === false ? 'off' : 'on'}
          onChange={(value) => update(page, { enableHdr: value === 'on' })}
          options={[
            { value: 'on', label: 'Record HDR when available' },
            { value: 'off', label: 'Always record in SDR' },
          ]}
        />
      </Field>

      <details className="settings-advanced">
        <summary>Encoder and bitrate</summary>
        <div className="settings-advanced-body">
          <Field label="Encoder" hint="The video encoder. Only encoders this machine supports are listed.">
            <SelectField
              value={encoder}
              onChange={(value) => update(page, { encoder: value })}
              options={encoderOptions(encoder, availableEncoders)}
            />
          </Field>

          <Field
            label="Rate control"
            hint={
              coercedFrom
                ? `${RATE_CONTROL_LABELS[coercedFrom]} is stored, but this encoder does not support it. Recordings use ${RATE_CONTROL_LABELS[rateControl]}.`
                : 'How the encoder spends its bits. Only the modes this encoder supports are listed.'
            }
          >
            <SelectField
              value={rateControl}
              onChange={(value) => update(page, { rateControl: value as RateControlMode })}
              options={rateControlOptions(encoder)}
            />
          </Field>

          {!usesQuantiser ? (
            <Field
              label="Bitrate"
              hint={
                rateControl === 'Cbr'
                  ? 'Kilobits per second, held constant whatever the picture costs. Around 15000 suits 1080p60.'
                  : 'Target kilobits per second. Around 15000 suits 1080p60.'
              }
            >
              <TextField
                value={bitrate}
                onChange={setBitrate}
                onBlur={commitBitrate}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    commitBitrate();
                  }
                }}
                inputMode="numeric"
                aria-label="Bitrate"
              />
            </Field>
          ) : null}

          {rateControl === 'Vbr' ? (
            <Field label="Maximum bitrate" hint="The ceiling for peaks, in kbps. 0 derives one from the target (1.5x).">
              <TextField
                value={maxBitrate}
                onChange={setMaxBitrate}
                onBlur={commitMaxBitrate}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    commitMaxBitrate();
                  }
                }}
                inputMode="numeric"
                aria-label="Maximum bitrate"
              />
            </Field>
          ) : null}

          <Field
            label="Maximum buffer size"
            hint="The most memory the replay buffer may use. Edited in MiB, stored as bytes."
          >
            <TextField
              type="number"
              min={1}
              value={bufferSizeMiB}
              onChange={setBufferSizeMiB}
              onBlur={commitBufferSize}
              onKeyDown={(event) => {
                if (event.key === 'Enter') {
                  commitBufferSize();
                }
              }}
              aria-label="Maximum buffer size"
            />
          </Field>
        </div>
      </details>

      {confirmBufferOff ? (
        <ConfirmDialog
          title="Turn off the replay buffer?"
          notice="Automatic highlights are on, and they need the buffer to capture the seconds before a moment. Turning off the buffer turns them off too. You can turn them back on from the Highlights tab."
          confirmLabel="Turn buffer off"
          cancelLabel="Keep buffer on"
          onConfirm={() => {
            update(page, { mode: 'Session', automaticClipsEnabled: false });
            setConfirmBufferOff(false);
          }}
          onCancel={() => setConfirmBufferOff(false)}
        />
      ) : null}
    </div>
  );
}
