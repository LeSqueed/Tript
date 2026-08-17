// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings page tests: each page renders its controls, editing a field sends the right
// UpdateSettings partial, the audio routing model behaves (assigning a source to a track,
// per-source volume, two sources on one track), the recording page's three selectors offer the right
// options (resolution presets plus this machine's display; frame-rate presets; only the encoders this
// machine registered) without coercing a stored value they do not offer, and every settings push
// lands on the model whether or not it echoes our own cause. The IPC client is exercised over a real
// IpcClient bound to a mock socket, so the wire shape is asserted on both the sent frames and the
// pushed content.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen, fireEvent, cleanup, act } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { SettingsView } from './SettingsView';
import type {
  AudioSourceKind,
  DisplayResolution,
  SettingsMessageContent,
} from '../settings/settingsModel';

/** The active socket — the app's live socket is last under StrictMode's double effect. */
function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

/** The full settings object pushed by the backend. */
function makeSettings(): SettingsMessageContent['settings'] {
  return {
    recording: {
      mode: 'Hybrid',
      resolutionWidth: 1920,
      resolutionHeight: 1080,
      fps: 60,
      encoder: 'x264',
      quality: 10,
      rateControl: 'Cqp',
      bitrateKbps: 15000,
      maxBitrateKbps: 0,
      outputDirectory: null,
    },
    buffer: { enabled: false, duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 },
    audio: {
      outputMode: 'Normal',
      devices: [
        { id: 'mic-1', name: 'Blue Yeti' },
        { id: 'speaker-1', name: 'Speakers' },
      ],
      tracks: [],
      mic: null,
      desktop: null,
    },
    capture: { method: 'Auto', display: null },
    game: { captureMode: 'Auto', gameCaptureTimeout: 10, gameList: [] },
  };
}

/**
 * Push a `settings` message. `availableEncoders` and `displayResolution` ride the content as
 * **siblings** of `settings`, exactly as AppHost.PushSettings sends them: they are facts about the
 * machine — its encoder registry and its primary display — not persisted settings, so neither is
 * nested under the recording page (RecordingSettings carries JsonExtensionData, so a nested field
 * would be round-tripped into the settings file). Pass `null` for a host that could not determine
 * one, and omit it for a backend that does not send the field at all.
 */
function pushSettings(
  ws: MockWebSocket,
  settings = makeSettings(),
  cause?: string,
  availableEncoders?: string[] | null,
  displayResolution?: DisplayResolution | null,
) {
  const content: SettingsMessageContent = { settings };
  if (cause !== undefined) {
    content.cause = cause;
  }
  if (availableEncoders !== undefined) {
    content.availableEncoders = availableEncoders;
  }
  if (displayResolution !== undefined) {
    content.displayResolution = displayResolution;
  }
  act(() => {
    ws.serverMessage(JSON.stringify({ method: 'settings', content }));
  });
}

function renderSettings() {
  const { factory } = createMockSocketFactory();
  const client = createIpcClient({ createSocket: factory });
  const result = render(<SettingsView client={client} />);
  client.connect();
  const ws = activeSocket();
  act(() => {
    ws.serverOpen();
  });
  pushSettings(ws);
  return { ...result, client, ws };
}

/** The `UpdateSettings` frames the app sent, in order. */
function sentUpdates(ws: MockWebSocket): Record<string, unknown>[] {
  return ws.sent
    .map((frame) => JSON.parse(frame))
    .filter((frame: { method?: string }) => frame.method === 'UpdateSettings')
    .map((frame: { parameters?: { settings?: Record<string, unknown> } }) => frame.parameters?.settings ?? {});
}

/** Set a control's value and commit it (React's controlled-input quirk: fire change then blur). */
function changeInput(label: RegExp, value: string) {
  const input = screen.getByLabelText(label) as HTMLInputElement;
  fireEvent.change(input, { target: { value } });
  fireEvent.blur(input);
}

beforeEach(() => {
  MockWebSocket.reset();
});

afterEach(() => {
  cleanup();
});

describe('SettingsView', () => {
  it('renders the five tabs and the recording page controls', () => {
    renderSettings();
    expect(screen.getByRole('tab', { name: 'Recording' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Buffer' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Audio' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Capture' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Game' })).toBeTruthy();
    expect(screen.getByLabelText(/^Recording mode/)).toBeTruthy();
    expect(screen.getByLabelText(/^Resolution/)).toBeTruthy();
    expect(screen.getByLabelText(/^Frame rate/)).toBeTruthy();
    expect(screen.getByLabelText(/^Encoder/)).toBeTruthy();
    expect(screen.getByLabelText(/^Rate control/)).toBeTruthy();
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
    expect(screen.getByLabelText(/^Output directory/)).toBeTruthy();
  });

  it('renders the buffer page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Buffer' }));
    expect(screen.getByLabelText('Enable rolling buffer')).toBeTruthy();
    expect(screen.getByLabelText(/^Buffer duration/)).toBeTruthy();
    expect(screen.getByLabelText(/^Maximum buffer size/)).toBeTruthy();
  });

  it('renders the audio page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    expect(screen.getByLabelText(/^Output mode/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add track' })).toBeTruthy();
  });

  it('renders the capture page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
    expect(screen.getByLabelText(/^Capture method/)).toBeTruthy();
  });

  it('renders the game page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Game' }));
    expect(screen.getByLabelText(/^Capture mode/)).toBeTruthy();
    expect(screen.getByLabelText(/^Game-capture timeout/)).toBeTruthy();
    expect(screen.getByPlaceholderText('Game name')).toBeTruthy();
  });

  it('sends a partial settings object, not the whole settings, when a field is edited', () => {
    const { ws } = renderSettings();
    changeInput(/^Frame rate/, '144');
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { fps: 144 } });
  });

  // ---- resolution ----
  //
  // The resolution the user picks is not only the encoder's scaled size: the host resets the OBS
  // canvas to it at startup (Program.BuildVideoSettings). Offering a size nobody chose would
  // therefore change what the next recording actually looks like, which is why the selector never
  // coerces a stored value it does not list.

  it('resolution is a selector over the common sizes', () => {
    const { ws } = renderSettings(); // no displayResolution field at all — an older backend
    const select = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
    ]);
    expect(select.value).toBe('1920x1080');
    // With no display reported there is nothing to mark, so every label is the bare size.
    expect(Array.from(select.options).map((option) => option.text)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
    ]);

    // An explicit null is the same "unknown": a host whose display detection failed says so rather
    // than inventing a size.
    pushSettings(ws, makeSettings(), 'server:init', undefined, null);
    const reread = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;
    expect(Array.from(reread.options).map((option) => option.text)).not.toContain('1920x1080 (display)');
  });

  it('picking a resolution sends both dimensions as a partial recording page', () => {
    const { ws } = renderSettings();
    fireEvent.change(screen.getByLabelText(/^Resolution/), { target: { value: '2560x1440' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { resolutionWidth: 2560, resolutionHeight: 1440 } });
  });

  it("the display's own size is marked, so the user can tell which option is their screen", () => {
    const onDisplay = makeSettings();
    onDisplay.recording.resolutionWidth = 2560;
    onDisplay.recording.resolutionHeight = 1440;
    const { ws } = renderSettings();
    pushSettings(ws, onDisplay, 'server:init', undefined, { width: 2560, height: 1440 });
    const select = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;

    // The display coincides with a preset here, so it adds no option — it marks the one it matches.
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
    ]);
    expect(Array.from(select.options).map((option) => option.text)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440 (display)',
      '3840x2160',
    ]);
    expect(select.value).toBe('2560x1440');
  });

  it('a display size outside the common list is offered, sorted by size', () => {
    const { ws } = renderSettings();
    // An ultrawide is not a 16:9 preset, and recording at a preset instead would letterbox or crop
    // the picture the user actually plays on.
    pushSettings(ws, makeSettings(), 'server:init', undefined, { width: 3440, height: 1440 });
    const select = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3440x1440',
      '3840x2160',
    ]);
    expect(select.options[3].text).toBe('3440x1440 (display)');

    fireEvent.change(select, { target: { value: '3440x1440' } });
    expect(sentUpdates(ws)[0]).toEqual({ recording: { resolutionWidth: 3440, resolutionHeight: 1440 } });
  });

  it('resolution keeps showing a stored size outside the list rather than coercing it', () => {
    const withCustom = makeSettings();
    withCustom.recording.resolutionWidth = 1600;
    withCustom.recording.resolutionHeight = 900;
    const { ws } = renderSettings();
    pushSettings(ws, withCustom, 'server:init', undefined, { width: 2560, height: 1440 });
    const select = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;

    // A `<select>` whose value matches no option renders its first option instead — 1280x720 here —
    // and the next unrelated edit would persist that. The stored size stays, marked custom.
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
      '1600x900',
    ]);
    expect(select.options[4].text).toBe('1600x900 (custom)');
    expect(select.value).toBe('1600x900');
    // Showing it must not persist it: nothing was sent just by rendering the page.
    expect(sentUpdates(ws)).toHaveLength(0);
  });

  it('frame rate is a selector over the common values, defaulting to 60', () => {
    const { ws } = renderSettings();
    const select = screen.getByLabelText(/^Frame rate/) as HTMLSelectElement;
    // Default 60 is offered and current.
    expect(Array.from(select.options).map((option) => option.value)).toEqual(['30', '60', '90', '144']);
    expect(select.value).toBe('60');

    // Picking a preset sends a partial update.
    fireEvent.change(select, { target: { value: '90' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { fps: 90 } });
  });

  it('frame rate keeps showing a non-preset value so a per-game override is not lost', () => {
    const withCustom = makeSettings();
    withCustom.recording.fps = 75;
    const { ws } = renderSettings();
    pushSettings(ws, withCustom, 'server:init');
    const select = screen.getByLabelText(/^Frame rate/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(['30', '60', '90', '144', '75']);
    expect(select.value).toBe('75');
  });

  it('encoder is a selector over the available encoders sent beside the settings', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_x264';
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264', 'ffmpeg_vaapi']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(['obs_x264', 'ffmpeg_vaapi']);
    expect(select.value).toBe('obs_x264');

    // Picking a supported encoder sends a partial update — and only the encoder, never the list.
    fireEvent.change(select, { target: { value: 'ffmpeg_vaapi' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { encoder: 'ffmpeg_vaapi' } });
  });

  it('encoder selector hides unsupported encoders but keeps the stored one selectable', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_nvenc_h264_tex'; // not registered on this machine
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    // Unsupported ids are not offered; the stored value is appended so the select cannot silently
    // render (and later persist) an encoder the user never chose.
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      'obs_x264',
      'obs_nvenc_h264_tex',
    ]);
    expect(select.value).toBe('obs_nvenc_h264_tex');
    // Marked custom, under the same human label an available NVENC id would carry: the label says
    // which encoder it is, "(custom)" says this machine does not have it.
    expect(select.options[1].text).toBe('NVIDIA (NVENC) (custom)');
  });

  it('encoder options are labelled by family while the id is what goes on the wire', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_x264';
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264', 'ffmpeg_vaapi', 'h264_texture_amf']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.text)).toEqual([
      'Software (x264)',
      'AMD/Intel (VAAPI)',
      'AMD (AMF)',
    ]);
    // The values are still the raw ids — the label is for the user, the id is for the backend.
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      'obs_x264',
      'ffmpeg_vaapi',
      'h264_texture_amf',
    ]);

    fireEvent.change(select, { target: { value: 'h264_texture_amf' } });
    expect(sentUpdates(ws)[0]).toEqual({ recording: { encoder: 'h264_texture_amf' } });
  });

  it('two encoders of one family keep their ids in the label so both are pickable', () => {
    const { ws } = renderSettings();
    // Both NVENC key sets are live at once on an OBS 31+ NVIDIA machine, so two options would
    // otherwise read "NVIDIA (NVENC)" and neither could be told from the other.
    pushSettings(ws, makeSettings(), 'server:init', ['jim_nvenc', 'obs_nvenc_h264_tex', 'obs_x264']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    const labels = Array.from(select.options).map((option) => option.text);

    expect(labels).toContain('NVIDIA (NVENC) — jim_nvenc');
    expect(labels).toContain('NVIDIA (NVENC) — obs_nvenc_h264_tex');
    expect(labels).toContain('Software (x264)');
  });

  it('an unrecognised encoder id is offered under its own id rather than hidden', () => {
    const { ws } = renderSettings();
    pushSettings(ws, makeSettings(), 'server:init', ['obs_x264', 'some_future_h264_encoder']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.text)).toContain('some_future_h264_encoder');
  });

  it('encoder falls back to the stored value plus obs_x264 when the list is unknown', () => {
    const { ws } = renderSettings(); // no availableEncoders field at all — an older backend
    const select = () => screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['x264', 'obs_x264']);
    expect(select().value).toBe('x264');

    // An explicit null is the same "unknown": the fake-recorder host never loads libobs, so it
    // cannot probe the encoder registry and says so rather than sending an empty list.
    pushSettings(ws, makeSettings(), 'server:init', null);
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['x264', 'obs_x264']);
    expect(select().value).toBe('x264');
  });

  // ---- rate control: the codec-and-quality surface ----
  //
  // The mode list is per-encoder because a mode name is written into the encoder's own rate_control
  // key, and a name a family does not know segfaults obs-ffmpeg's VAAPI encoder. The backend coerces
  // anything unsupported, so these tests are about the page not *offering* a choice the backend would
  // override — not about the crash itself, which is pinned in the recorder's own tests.

  it('rate control offers only the modes the selected encoder supports', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_x264';
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264', 'ffmpeg_vaapi']);
    const select = () => screen.getByLabelText(/^Rate control/) as HTMLSelectElement;

    // x264: CRF is its constant-quality mode, and it has no CQP mode at all.
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['Crf', 'Cbr', 'Vbr']);
    expect(Array.from(select().options).map((option) => option.text)).toEqual([
      'Constant quality (CRF)',
      'Constant bitrate (CBR)',
      'Variable bitrate (VBR)',
    ]);

    // VAAPI: CQP rather than CRF, and no VBR — the specification has no VAAPI table, so its VBR
    // ceiling key is not something we are willing to guess at.
    const withVaapi = makeSettings();
    withVaapi.recording.encoder = 'ffmpeg_vaapi';
    pushSettings(ws, withVaapi, 'server:init', ['obs_x264', 'ffmpeg_vaapi']);
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['Cqp', 'Cbr']);
  });

  it('rate control change sends a partial recording page', () => {
    const { ws } = renderSettings();
    fireEvent.change(screen.getByLabelText(/^Rate control/), { target: { value: 'Cbr' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { rateControl: 'Cbr' } });
  });

  it('the quality profile is shown for constant quality and the bitrate for CBR', () => {
    const { ws } = renderSettings();
    // Default is constant quality: the quality preset selector is the control, and no bitrate field
    // exists to be filled in for a mode that does not read one.
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
    expect(screen.queryByLabelText(/^Bitrate/)).toBeNull();
    expect(screen.queryByLabelText(/^Maximum bitrate/)).toBeNull();

    const cbr = makeSettings();
    cbr.recording.rateControl = 'Cbr';
    pushSettings(ws, cbr, 'server:init');
    expect(screen.queryByLabelText(/^Quality/)).toBeNull();
    expect((screen.getByLabelText(/^Bitrate/) as HTMLInputElement).value).toBe('15000');
    // CBR has no ceiling: max_bitrate is a VBR-only key on every family that has one at all.
    expect(screen.queryByLabelText(/^Maximum bitrate/)).toBeNull();
  });

  it('VBR adds the ceiling field alongside the target bitrate', () => {
    const { ws } = renderSettings();
    const vbr = makeSettings();
    vbr.recording.encoder = 'obs_nvenc_h264_tex';
    vbr.recording.rateControl = 'Vbr';
    vbr.recording.maxBitrateKbps = 24000;
    pushSettings(ws, vbr, 'server:init', ['obs_nvenc_h264_tex']);

    expect((screen.getByLabelText(/^Bitrate/) as HTMLInputElement).value).toBe('15000');
    expect((screen.getByLabelText(/^Maximum bitrate/) as HTMLInputElement).value).toBe('24000');
  });

  it('a bitrate edit commits on blur and sends kbps', () => {
    const { ws } = renderSettings();
    const cbr = makeSettings();
    cbr.recording.rateControl = 'Cbr';
    pushSettings(ws, cbr, 'server:init');

    changeInput(/^Bitrate/, '30000');
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { bitrateKbps: 30000 } });
  });

  it('a non-numeric bitrate draft is discarded rather than sent', () => {
    const { ws } = renderSettings();
    const cbr = makeSettings();
    cbr.recording.rateControl = 'Cbr';
    pushSettings(ws, cbr, 'server:init');

    changeInput(/^Bitrate/, 'lots');
    expect(sentUpdates(ws)).toHaveLength(0);
    // The field re-syncs to the stored value, so the user is never left looking at a draft that was
    // silently dropped.
    expect((screen.getByLabelText(/^Bitrate/) as HTMLInputElement).value).toBe('15000');
  });

  it('a stored mode the encoder cannot use is shown coerced, and nothing is sent', () => {
    const { ws } = renderSettings();
    // A settings file written on a software-only machine carries CRF; here the encoder is VAAPI,
    // whose family rejects that string. The recorder coerces it to CQP, so the page shows CQP —
    // displaying CRF would be a selector lying about what the next recording will do.
    const carried = makeSettings();
    carried.recording.encoder = 'ffmpeg_vaapi';
    carried.recording.rateControl = 'Crf';
    pushSettings(ws, carried, 'server:init', ['ffmpeg_vaapi']);

    const select = screen.getByLabelText(/^Rate control/) as HTMLSelectElement;
    expect(select.value).toBe('Cqp');
    expect(Array.from(select.options).map((option) => option.value)).not.toContain('Crf');
    // Showing the coerced value must not persist it: the stored choice is the user's, and rewriting
    // it on their behalf would lose it the moment they moved the config back to the other machine.
    expect(sentUpdates(ws)).toHaveLength(0);
    expect(screen.getByText(/does not support it/)).toBeTruthy();
  });

  it('rate control renders from the defaults when the backend sends no mode at all', () => {
    const { ws } = renderSettings();
    const older = makeSettings();
    delete older.recording.rateControl;
    delete older.recording.bitrateKbps;
    pushSettings(ws, older, 'server:init');

    // An older backend's push carries neither field. The selector still renders, on the safe
    // constant-quality default, rather than showing an empty value.
    expect((screen.getByLabelText(/^Rate control/) as HTMLSelectElement).value).toBe('Cqp');
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
  });

  it('output directory edit sends a partial recording page', () => {
    const { ws } = renderSettings();
    fireEvent.change(screen.getByLabelText(/^Output directory/), {
      target: { value: '/home/tester/Videos/Tript' },
    });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { outputDirectory: '/home/tester/Videos/Tript' } });
  });

  it('clearing the output directory sends null (use the platform default)', () => {
    const { ws } = renderSettings();
    const withDirectory = makeSettings();
    withDirectory.recording.outputDirectory = '/home/tester/Videos/Tript';
    pushSettings(ws, withDirectory);
    fireEvent.change(screen.getByLabelText(/^Output directory/), { target: { value: '' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { outputDirectory: null } });
  });

  it('browse sends SetVideoLocation (no parameters) and a push fills the field', () => {
    const { ws } = renderSettings();

    // Clicking Browse asks the backend to open its native folder picker; the command carries no
    // parameters, so the frame has no `parameters` field at all.
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    const browseFrame = JSON.parse(ws.sent[ws.sent.length - 1]);
    expect(browseFrame).toEqual({ method: 'SetVideoLocation' });

    // The backend persists the picked directory and broadcasts a full settings push. A foreign
    // cause (not our own echo) re-syncs the page, so the field shows the picked path.
    const picked = makeSettings();
    picked.recording.outputDirectory = '/home/tester/Videos/Picked';
    pushSettings(ws, picked, 'server:picked');
    expect((screen.getByLabelText(/^Output directory/) as HTMLInputElement).value).toBe(
      '/home/tester/Videos/Picked',
    );
  });

  it('recording mode change sends a partial recording page', () => {
    const { ws } = renderSettings();
    fireEvent.change(screen.getByLabelText(/^Recording mode/), { target: { value: 'Session' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { mode: 'Session' } });
  });

  it('audio page: adding a track sends a track with empty sources', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    const tracks = (sent[0] as { audio: { tracks: unknown[] } }).audio.tracks;
    expect(tracks).toHaveLength(1);
    expect((tracks[0] as { name: string; sources: unknown[] }).name).toBe('Track 1');
    expect((tracks[0] as { sources: unknown[] }).sources).toEqual([]);
  });

  it('audio page: assigning a source to a track routes it with default volume 1', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));
    const sent1 = sentUpdates(ws);
    const tracks1 = (sent1[0] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    expect(tracks1).toHaveLength(1);

    // Push the new track back (as the backend would) so the UI re-renders with one track.
    const withTrack = makeSettings();
    withTrack.audio.tracks = tracks1;
    pushSettings(ws, withTrack, 'tript:audio:1');

    fireEvent.click(screen.getByRole('button', { name: '+ Add source' }));
    const sent2 = sentUpdates(ws);
    expect(sent2).toHaveLength(2);
    const tracks2 = (sent2[1] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    expect(tracks2).toHaveLength(1);
    expect(tracks2[0].sources).toHaveLength(1);
    expect(tracks2[0].sources[0].name).toBe('Microphone');
    expect(tracks2[0].sources[0].kind).toBe('Input');
    expect(tracks2[0].sources[0].volume).toBe(1);
  });

  it('audio page: two sources can be routed onto one track', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));
    let tracks = (sentUpdates(ws)[0] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    let withTrack = makeSettings();
    withTrack.audio.tracks = tracks;
    pushSettings(ws, withTrack, 'tript:audio:1');

    fireEvent.click(screen.getByRole('button', { name: '+ Add source' }));
    tracks = (sentUpdates(ws)[1] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    withTrack = makeSettings();
    withTrack.audio.tracks = tracks;
    pushSettings(ws, withTrack, 'tript:audio:2');

    // The second add still offers the system source; add it.
    fireEvent.click(screen.getByRole('button', { name: '+ Add source' }));
    tracks = (sentUpdates(ws)[2] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    expect(tracks[0].sources).toHaveLength(2);
    expect(tracks[0].sources[1].name).toBe('System output');
  });

  it('audio page: per-source volume is adjustable, volume is per-source not per-track', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));
    let tracks = (sentUpdates(ws)[0] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    let withTrack = makeSettings();
    withTrack.audio.tracks = tracks;
    pushSettings(ws, withTrack, 'tript:audio:1');

    fireEvent.click(screen.getByRole('button', { name: '+ Add source' }));
    tracks = (sentUpdates(ws)[1] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    withTrack = makeSettings();
    withTrack.audio.tracks = tracks;
    pushSettings(ws, withTrack, 'tript:audio:2');

    // Add a second source to the same track.
    fireEvent.click(screen.getByRole('button', { name: '+ Add source' }));
    tracks = (sentUpdates(ws)[2] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    withTrack = makeSettings();
    withTrack.audio.tracks = tracks;
    pushSettings(ws, withTrack, 'tript:audio:3');

    const sliders = screen.getAllByRole('slider');
    expect(sliders).toHaveLength(2);
    fireEvent.change(sliders[0], { target: { value: '0.4' } });

    const finalTracks = (sentUpdates(ws)[3] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    expect(finalTracks[0].sources).toHaveLength(2);
    expect(finalTracks[0].sources[0].volume).toBeCloseTo(0.4);
    expect(finalTracks[0].sources[1].volume).toBe(1);
  });

  it('every push is applied to the model, whether it echoes our own cause or not', () => {
    const { ws } = renderSettings();
    const frameRate = () => screen.getByLabelText(/^Frame rate/) as HTMLSelectElement;
    // The user picks a frame rate; the send is tagged with our cause.
    changeInput(/^Frame rate/, '144');
    expect(sentUpdates(ws)).toHaveLength(1);

    // The backend echoes the full model with the same cause. The echo is its acceptance of the
    // change and carries the newly-persisted values, so it must render — the cause only gates the
    // re-sync of free-text drafts (useSettings.ts), and a selector has no draft to protect.
    const echo = makeSettings();
    echo.recording.fps = 144;
    pushSettings(ws, echo, 'tript:recording:1');
    expect(frameRate().value).toBe('144');

    // A push with a foreign cause is a real external change and equally replaces the model.
    const external = makeSettings();
    external.recording.fps = 30;
    pushSettings(ws, external, 'otherProcess:init');
    expect(frameRate().value).toBe('30');
  });

  it('reads from the settings message, not a stale shadow copy', () => {
    const { ws } = renderSettings();
    // A second external push changes the mode; the UI reflects it.
    const updated = makeSettings();
    updated.recording.mode = 'Buffer';
    pushSettings(ws, updated, 'server:changed');
    fireEvent.click(screen.getByRole('tab', { name: 'Recording' }));
    expect((screen.getByLabelText(/^Recording mode/) as HTMLSelectElement).value).toBe('Buffer');
  });

  it('forms stay editable before the backend pushes settings', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    render(<SettingsView client={client} />);
    client.connect();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
    });
    // No settings push yet — the page still renders with defaults and is editable.
    expect(screen.getByLabelText(/^Frame rate/)).toBeTruthy();
    changeInput(/^Frame rate/, '144');
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { fps: 144 } });
  });
});

interface AudioTrackLike {
  id: string;
  name: string;
  sources: {
    name: string;
    kind: AudioSourceKind;
    volume: number;
    label?: string;
    deviceId?: string;
    [key: string]: unknown;
  }[];
  [key: string]: unknown;
}
