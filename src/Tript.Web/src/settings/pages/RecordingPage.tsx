// SPDX-License-Identifier: GPL-2.0-or-later
//
// The recording page: the session recording itself. Mode (session/buffer/hybrid), resolution, frame
// rate, and the codec-and-quality surface — which encoder, how it is told to spend its bits (rate
// control), and the quality or bitrate that mode reads.

import { useEffect, useState } from 'react';
import type { SettingsPageName } from '../useSettings';
import type {
  DisplayResolution,
  RateControlMode,
  RecordingMode,
  RecordingSettings,
} from '../settingsModel';
import { ActionButton, Field, GhostButton, SelectField, TextField, type SelectOption } from '../form';

const RECORDING_MODES: { value: RecordingMode; label: string }[] = [
  { value: 'Session', label: 'Session — one continuous recording' },
  { value: 'Buffer', label: 'Buffer — rolling replay only, nothing written until saved' },
  { value: 'Hybrid', label: 'Hybrid — session and rolling buffer at once' },
];

/**
 * The quality profile applied when a game has no override of its own. The numbers are the backend's
 * 1..20 scale; the recorder maps them onto the H.264 quantiser scale the resolved encoder family
 * reads (`ObsRecorderSession.MapQualityToQuantiser`), so these four presets land at CRF/QP 28, 23, 20
 * and 16 — the band where H.264 game footage is worth keeping.
 */
const QUALITY_OPTIONS: SelectOption[] = [
  { value: '3', label: 'Low' },
  { value: '5', label: 'Medium' },
  { value: '10', label: 'High' },
  { value: '18', label: 'Max' },
];

/**
 * The common frame rates OBS accepts (libobs takes the FPS as an fps_num/fps_den fraction, so
 * every integer rate here is fine). 60 is the default.
 */
const FPS_PRESETS = [30, 60, 90, 144];

/**
 * The common 16:9 recording sizes, smallest first. Every entry is a multiple of four wide and of two
 * high, which is what libobs silently rounds to — it accepts 1366 and records 1364 while still
 * reporting success, so a preset list is also how the page avoids offering a size that quietly
 * becomes a different one.
 */
const RESOLUTION_PRESETS: [number, number][] = [
  [1280, 720],
  [1920, 1080],
  [2560, 1440],
  [3840, 2160],
];

/**
 * The software encoder every libobs build registers. Offered as the floor when the machine's real
 * encoder set is unknown, so the selector is never empty.
 */
const FALLBACK_ENCODER = 'obs_x264';

/**
 * The settings model's encoder default. It is a placeholder meaning "the backend decides" — the real
 * software id is `obs_x264` — so it is neither labelled as x264 nor treated as a family whose
 * rate-control vocabulary is known (`ObsRecorderSession.ResolveVideoEncoderId` prefers a hardware
 * encoder when one is registered, so what this resolves to is not knowable from the frontend).
 */
const BACKEND_DECIDES_ENCODER = 'x264';

/** The encoder families whose key sets — and therefore whose rate-control vocabularies — differ. */
type EncoderFamily = 'x264' | 'vaapi' | 'nvenc' | 'amf' | 'qsv' | 'unknown';

/**
 * Which family an encoder id belongs to, mirroring `ObsRecorderSession.ClassifyFamily`: x264 matched
 * exactly and the rest by substring, because each hardware family ships several ids while "contains
 * x264" would also catch a third-party id that merely mentions it.
 */
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

/** The human name for each family. Unknown has none — an unrecognised id is shown as itself. */
const FAMILY_LABELS: Record<EncoderFamily, string> = {
  x264: 'Software (x264)',
  // VAAPI is the Linux hardware path for both AMD and Intel GPUs, so it is not one vendor's encoder.
  vaapi: 'AMD/Intel (VAAPI)',
  nvenc: 'NVIDIA (NVENC)',
  amf: 'AMD (AMF)',
  qsv: 'Intel (QSV)',
  unknown: '',
};

/**
 * The modes each family accepts, in the order they are offered. Mirrored from
 * `ObsRecorderSession.SupportedRateControlModes`, which is the authority — see the file header for
 * why this copy is a convenience rather than the safety property.
 */
const RATE_CONTROL_MODES: Record<EncoderFamily, RateControlMode[]> = {
  // CRF is x264's spelling of constant quality and exists in no other family; x264 has no CQP mode.
  x264: ['Crf', 'Cbr', 'Vbr'],
  // VAAPI is held to the two modes actually evidenced on a real runtime. Its
  // VBR ceiling key is unknown to us, and a mistyped key fails silently at the wrong bitrate.
  vaapi: ['Cqp', 'Cbr'],
  nvenc: ['Cqp', 'Cbr', 'Vbr'],
  amf: ['Cqp', 'Cbr', 'Vbr'],
  qsv: ['Cqp', 'Cbr', 'Vbr'],
  // An id no table describes: constant quality plus CBR, the one mode every documented family
  // accepts. Also where the "backend decides" placeholder lands, since what it resolves to is not
  // knowable here.
  unknown: ['Cqp', 'Cbr'],
};

const RATE_CONTROL_LABELS: Record<RateControlMode, string> = {
  Crf: 'Constant quality (CRF)',
  Cqp: 'Constant quality (CQP)',
  Cbr: 'Constant bitrate (CBR)',
  Vbr: 'Variable bitrate (VBR)',
};

/** The modes that read the quality profile rather than a bitrate. */
const QUANTISER_MODES: RateControlMode[] = ['Crf', 'Cqp'];

/**
 * Appends the stored value to `options` when it is not already one of them. A `<select>` whose
 * value matches no option renders its *first* option instead, which would show the user a setting
 * they do not have — and the next unrelated edit would then persist that lie.
 */
function withStoredValue(options: SelectOption[], stored: string): SelectOption[] {
  if (!stored || options.some((option) => option.value === stored)) {
    return options;
  }
  return [...options, { value: stored, label: `${stored} (custom)` }];
}

/**
 * The wire form of a resolution in this selector: `<width>x<height>`, which is also what the user
 * reads. The option *value* has to be a single string because that is what a `<select>` carries, and
 * the two numbers are split back out when the choice is sent.
 */
function resolutionValue(width: number, height: number): string {
  return `${width}x${height}`;
}

/** The two numbers back out of an option value, or null when it is not one of ours. */
function parseResolution(value: string): { width: number; height: number } | null {
  const match = /^(\d+)x(\d+)$/.exec(value);
  return match ? { width: Number(match[1]), height: Number(match[2]) } : null;
}

/**
 * The resolution options: the common sizes, plus this machine's primary display when it is not one
 * of them, plus the stored size when it is outside both. The display's own size is marked
 * "(display)" wherever it appears — including when it coincides with a preset, because "which of
 * these is my screen" is the question the label answers and 2560x1440 looks no different from
 * 1920x1080 without it.
 */
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

/**
 * The frame-rate options: the presets, plus the stored rate when it is not one of them. A
 * non-finite rate is a corrupt setting rather than a custom one, so it is not offered as a choice.
 */
function fpsOptions(stored: number): SelectOption[] {
  const presets = FPS_PRESETS.map((fps) => ({ value: String(fps), label: String(fps) }));
  return Number.isFinite(stored) ? withStoredValue(presets, String(stored)) : presets;
}

/**
 * The label for one encoder id among a set of them: the family's human name, disambiguated by the raw
 * id when the same family registered more than one (both NVENC key sets are live at once on OBS 31+).
 * An id from a family nothing here recognises keeps its raw id as its label, so a real encoder is
 * never hidden behind a guess — and the "backend decides" placeholder says what it actually does.
 */
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
  return sameFamily > 1 ? `${label} — ${id}` : label;
}

/**
 * The encoder options: exactly the ids this machine registered, so unsupported encoders are hidden
 * rather than offered and refused at record time. `available` is undefined when the backend could
 * not tell us (see the file header) — then offer the stored value plus the software encoder.
 */
function encoderOptions(stored: string, available?: string[]): SelectOption[] {
  const ids = available?.length ? available : [stored, FALLBACK_ENCODER].filter(Boolean);
  const unique = [...new Set(ids)];
  const options = unique.map((id) => ({ value: id, label: encoderLabel(id, unique) }));
  if (!stored || unique.includes(stored)) {
    return options;
  }
  // The stored encoder is not registered here — a pulled plugin, a swapped GPU, or a config written
  // on another machine. It stays selectable and marked, rather than being silently replaced by
  // whatever happens to be first in the list.
  return [...options, { value: stored, label: `${encoderLabel(stored, [stored])} (custom)` }];
}

/** The rate-control options for an encoder: only what that family accepts. */
function rateControlOptions(encoderId: string): SelectOption[] {
  return RATE_CONTROL_MODES[encoderFamily(encoderId)].map((mode) => ({
    value: mode,
    label: RATE_CONTROL_LABELS[mode],
  }));
}

/**
 * The mode the recording will actually use: the stored one when the selected encoder's family accepts
 * it, else that family's constant-quality mode — exactly the coercion
 * `ObsRecorderSession.CoerceRateControlMode` performs. Showing the coerced value is what keeps the
 * page honest: the alternative is a selector displaying CRF while the recorder writes CQP.
 */
function effectiveRateControl(stored: RateControlMode | undefined, encoderId: string): RateControlMode {
  const modes = RATE_CONTROL_MODES[encoderFamily(encoderId)];
  return stored && modes.includes(stored) ? stored : modes[0];
}

/** A whole number of kbps, or null when the draft is not one. */
function parseKbps(draft: string): number | null {
  const match = /^\s*(\d+)\s*$/.exec(draft);
  return match ? Number(match[1]) : null;
}

export function RecordingPage({
  settings,
  update,
  page,
  externalPushCount,
  availableEncoders,
  displayResolution,
  onBrowse,
}: {
  settings: RecordingSettings;
  update: (page: SettingsPageName, patch: Partial<Record<string, unknown>>) => void;
  page: SettingsPageName;
  externalPushCount: number;
  /**
   * The encoder ids this machine registered, from the settings push (a sibling of `settings`, not a
   * field of it). Undefined means the backend could not tell us; the selector then falls back.
   */
  availableEncoders?: string[];
  /**
   * The primary display's size, from the settings push (a sibling of `settings` for the same reason).
   * Undefined when the host could not detect a display; the selector then offers the presets alone.
   */
  displayResolution?: DisplayResolution;
  /** Opens the native folder picker. The picked directory arrives as a settings push, like any edit. */
  onBrowse: () => void;
}) {
  const [bitrate, setBitrate] = useState<string>(String(settings.bitrateKbps ?? ''));
  const [maxBitrate, setMaxBitrate] = useState<string>(String(settings.maxBitrateKbps ?? ''));

  // Re-sync drafts from the model only on an external push, never on the echo of our own edit.
  useEffect(() => {
    setBitrate(String(settings.bitrateKbps ?? ''));
    setMaxBitrate(String(settings.maxBitrateKbps ?? ''));
  }, [externalPushCount, settings.bitrateKbps, settings.maxBitrateKbps]);

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
      update(page, { bitrateKbps: kbps });
    }
  }

  function commitMaxBitrate() {
    const kbps = parseKbps(maxBitrate);
    if (kbps === null) {
      setMaxBitrate(String(settings.maxBitrateKbps ?? ''));
    } else {
      update(page, { maxBitrateKbps: kbps });
    }
  }

  const encoder = settings.encoder;
  const rateControl = effectiveRateControl(settings.rateControl, encoder);
  // A stored mode this encoder cannot use is not an error to correct on the user's behalf — the page
  // says which mode the recording will use and leaves the stored value alone, exactly as it does for
  // an out-of-list encoder. Changing it without being asked would persist a choice nobody made.
  const coercedFrom = settings.rateControl && settings.rateControl !== rateControl ? settings.rateControl : undefined;
  const usesQuantiser = QUANTISER_MODES.includes(rateControl);

  return (
    <div className="settings-page" data-page="recording">
      <Field label="Recording mode" hint="Hybrid is the default, globally and per game.">
        <SelectField
          value={settings.mode}
          onChange={(value) => update(page, { mode: value as RecordingMode })}
          options={RECORDING_MODES}
        />
      </Field>

      <Field
        label="Resolution"
        hint={
          displayResolution
            ? 'Width × height of the recorded picture. Your display’s own size is marked; a stored size outside this list is kept and marked custom.'
            : 'Width × height of the recorded picture. A stored size outside this list is kept and marked custom.'
        }
      >
        <SelectField
          value={resolutionValue(settings.resolutionWidth, settings.resolutionHeight)}
          onChange={commitResolution}
          options={resolutionOptions(settings.resolutionWidth, settings.resolutionHeight, displayResolution)}
        />
      </Field>

      <Field label="Frame rate" hint="Frames per second. A stored rate outside this list is kept and marked custom.">
        <SelectField
          value={String(settings.fps)}
          onChange={(value) => update(page, { fps: Number(value) })}
          options={fpsOptions(settings.fps)}
        />
      </Field>

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
            ? `${RATE_CONTROL_LABELS[coercedFrom]} is stored but this encoder does not support it; recordings use ${RATE_CONTROL_LABELS[rateControl]}.`
            : 'How the encoder spends its bits. Only the modes this encoder supports are listed.'
        }
      >
        <SelectField
          value={rateControl}
          onChange={(value) => update(page, { rateControl: value as RateControlMode })}
          options={rateControlOptions(encoder)}
        />
      </Field>

      {usesQuantiser ? (
        <Field label="Quality" hint="Constant quality: the encoder spends whatever the picture needs. Applied when a game has no override of its own.">
          <SelectField
            value={String(settings.quality)}
            onChange={(value) => update(page, { quality: Number(value) })}
            options={QUALITY_OPTIONS}
          />
        </Field>
      ) : (
        <Field
          label="Bitrate"
          hint={
            rateControl === 'Cbr'
              ? 'Kbps held constant whatever the picture costs. Around 15000 suits 1080p60.'
              : 'Target kbps. Around 15000 suits 1080p60.'
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
      )}

      {rateControl === 'Vbr' ? (
        <Field label="Maximum bitrate" hint="The ceiling for peaks, in kbps. 0 derives one from the target (1.5×).">
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

      <Field label="Output directory" hint="Where recordings are saved. Leave empty for the default (Videos/Tript).">
        <span className="settings-row">
          {/* Explicit aria-label: this is the only field sharing its <label> with a second
              control, so its derived name would otherwise absorb the Browse button's text. */}
          <TextField
            value={settings.outputDirectory ?? ''}
            onChange={(value) => update(page, { outputDirectory: value === '' ? null : value })}
            placeholder="e.g. D:\\Recordings"
            aria-label="Output directory"
          />
          <GhostButton onClick={onBrowse} title="Choose the recording folder with a native picker">
            Browse
          </GhostButton>
        </span>
      </Field>

      <div className="settings-actions">
        <ActionButton onClick={() => update(page, { mode: 'Hybrid' })}>Reset to defaults</ActionButton>
      </div>
    </div>
  );
}
