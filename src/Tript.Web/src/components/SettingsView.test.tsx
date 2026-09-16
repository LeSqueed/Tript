// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen, fireEvent, cleanup, act, within } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { SettingsView } from './SettingsView';
import { ToastProvider } from './ui/toast/ToastProvider';
import type {
  AudioSourceKind,
  CaptureSettings,
  DisplayInfo,
  DisplayResolution,
  SettingsMessageContent,
} from '../settings/settingsModel';

function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

function makeSettings(): SettingsMessageContent['settings'] {
  return {
    recording: {
      mode: 'SessionWithReplayBuffer',
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
    general: {
      startWithWindows: false,
      startupVisibility: 'Window',
      minimizeBehavior: 'Taskbar',
      closeBehavior: 'Exit',
      notifications: {
        enabled: true,
        recordingStarted: true,
        recordingStartedSound: true,
        recordingStopped: true,
        recordingStoppedSound: true,
        errors: true,
        errorsSound: true,
      },
    },
    hotkeys: {
      enabled: true,
      toggleRecording: { modifiers: ['Control', 'Alt'], key: 'KeyR' },
      manualBookmark: { modifiers: ['Control', 'Alt'], key: 'KeyB' },
      quickClip: { modifiers: ['Control', 'Alt'], key: 'KeyC' },
      quickClipSeconds: 30,
    },
  };
}

function pushSettings(
  ws: MockWebSocket,
  settings = makeSettings(),
  cause?: string,
  availableEncoders?: string[] | null,
  displayResolution?: DisplayResolution | null,
  availableDisplays?: DisplayInfo[] | null,
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
  if (availableDisplays !== undefined) {
    content.availableDisplays = availableDisplays;
  }
  act(() => {
    ws.serverMessage(JSON.stringify({ method: 'settings', content }));
  });
}

function renderSettings(initialPage: 'recording' | 'general' = 'recording') {
  const { factory } = createMockSocketFactory();
  const client = createIpcClient({ createSocket: factory });
  const result = render(<ToastProvider><SettingsView client={client} /></ToastProvider>);
  client.connect();
  const ws = activeSocket();
  act(() => {
    ws.serverOpen();
  });
  pushSettings(ws);
  fireEvent.click(screen.getByRole('tab', { name: initialPage === 'general' ? 'General' : 'Recording' }));
  return { ...result, client, ws };
}

function sentUpdates(ws: MockWebSocket): Record<string, unknown>[] {
  return ws.sent
    .map((frame) => JSON.parse(frame))
    .filter((frame: { method?: string }) => frame.method === 'UpdateSettings')
    .map((frame: { parameters?: { settings?: Record<string, unknown> } }) => frame.parameters?.settings ?? {});
}

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
  it('opens directly to the Games tab when a game focus is requested', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    render(<ToastProvider><SettingsView client={client} focusGameId="custom-existing" /></ToastProvider>);
    client.connect();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
    });
    pushSettings(ws);

    expect(screen.getByRole('tab', { name: 'Games' }).getAttribute('aria-selected')).toBe('true');
  });

  it('renders the seven tabs and the recording page controls', () => {
    renderSettings('general');
    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual([
      'General',
      'Recording',
      'Highlights',
      'Audio',
      'Capture',
      'Games',
      'Hotkeys',
    ]);
    expect(screen.getByRole('tab', { name: 'Recording' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Highlights' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Audio' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Capture' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Games' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'General' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'Hotkeys' })).toBeTruthy();
    expect(screen.getByRole('tab', { name: 'General' }).getAttribute('aria-selected')).toBe('true');
    fireEvent.click(screen.getByRole('tab', { name: 'Recording' }));
    expect(screen.getByLabelText(/^Recording mode/)).toBeTruthy();
    expect(screen.getByLabelText(/^Resolution/)).toBeTruthy();
    expect(screen.getByLabelText(/^Frame rate/)).toBeTruthy();
    expect(screen.getByLabelText(/^Encoder/)).toBeTruthy();
    expect(screen.getByLabelText(/^Rate control/)).toBeTruthy();
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
    expect(screen.getByLabelText(/^HDR/)).toBeTruthy();
    expect(screen.getByLabelText(/^Output directory/)).toBeTruthy();
  });

  it('renders General settings and sends page-scoped updates', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'General' }));

    expect(screen.getByLabelText('Start with Windows')).toBeTruthy();
    expect(screen.getByLabelText(/^Startup visibility/)).toBeTruthy();
    expect(screen.getByLabelText(/^When closing Tript/)).toBeTruthy();
    expect(screen.getByLabelText('Enable desktop notifications')).toBeTruthy();
    expect(screen.queryByLabelText(/^Startup destination/)).toBeNull();
    expect(screen.queryByLabelText(/^Close while recording/)).toBeNull();
    expect(screen.queryByText('Unfinished recording found')).toBeNull();
    expect(within(screen.getByText('Errors').closest('.field') as HTMLElement).getByLabelText('Play sound')).toBeTruthy();

    fireEvent.click(screen.getByLabelText('Start with Windows'));
    expect(sentUpdates(ws).at(-1)).toEqual({ general: { startWithWindows: true } });

    fireEvent.change(screen.getByLabelText(/^Startup visibility/), { target: { value: 'Tray' } });
    expect(sentUpdates(ws).at(-1)).toEqual({ general: { startupVisibility: 'Tray' } });
  });

  it('sends notification updates as a nested general patch', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'General' }));
    const errorsField = screen.getByText('Errors').closest('.field') as HTMLElement;
    fireEvent.click(within(errorsField).getByLabelText('Show notification'));

    expect(sentUpdates(ws).at(-1)).toEqual({
      general: {
        notifications: {
          errors: false,
        },
      },
    });
  });

  it('disables notification-specific options when notifications are disabled', () => {
    const { ws } = renderSettings('general');
    fireEvent.click(screen.getByLabelText('Enable desktop notifications'));
    const disabled = makeSettings();
    disabled.general.notifications.enabled = false;
    pushSettings(ws, disabled);

    for (const fieldLabel of ['Recording started', 'Recording stopped', 'Errors']) {
      const field = screen.getByText(fieldLabel).closest('.field') as HTMLElement;
      for (const toggleLabel of ['Show notification', 'Play sound']) {
        expect((within(field).getByLabelText(toggleLabel) as HTMLInputElement).disabled).toBe(true);
      }
    }
  });

  it('fills General defaults when an older backend omits the page', () => {
    const { ws } = renderSettings();
    const older = makeSettings();
    delete (older as Partial<typeof older>).general;
    pushSettings(ws, older, 'older-backend');
    fireEvent.click(screen.getByRole('tab', { name: 'General' }));

    expect((screen.getByLabelText(/^Startup visibility/) as HTMLSelectElement).value).toBe('Window');
    expect((screen.getByLabelText('Start with Windows') as HTMLInputElement).checked).toBe(false);
  });

  it('keeps the recording mode default when an older settings push omits it', () => {
    const { ws } = renderSettings();
    const older = makeSettings();
    delete (older.recording as Partial<typeof older.recording>).mode;
    pushSettings(ws, older, 'older-backend');
    fireEvent.click(screen.getByRole('tab', { name: 'Recording' }));

    expect((screen.getByLabelText(/^Recording mode/) as HTMLSelectElement).value).toBe('SessionWithReplayBuffer');
  });

  it('describes the replay buffer on the highlights page', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Highlights' }));
    expect(screen.getByText(/runs in Session \+ replay buffer and Replay buffer only modes/i)).toBeTruthy();
  });

  it('keeps the technical labels technical', () => {
    renderSettings();
    for (const label of ['Encoder', 'Rate control', 'Frame rate', 'Resolution', 'Recording mode']) {
      expect(screen.getByText(label, { selector: '.field-label' })).toBeTruthy();
    }
  });

  it('shows HDR as on when the push carries no enableHdr', () => {
    renderSettings();
    expect((screen.getByLabelText(/^HDR/) as HTMLSelectElement).value).toBe('on');
  });

  it('sends enableHdr false when HDR is turned off', () => {
    const { ws } = renderSettings();
    changeInput(/^HDR/, 'off');
    expect(sentUpdates(ws).at(-1)).toMatchObject({ recording: { enableHdr: false } });
  });

  it('renders the highlights page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Highlights' }));
    expect(screen.getByLabelText(/^Buffer length/)).toBeTruthy();
    expect(screen.getByLabelText(/^Automatic highlights/)).toBeTruthy();
    expect(screen.getByLabelText(/^Seconds before each highlight/)).toBeTruthy();
    expect(screen.getByLabelText(/^Seconds after each highlight/)).toBeTruthy();
    expect(screen.queryByLabelText(/^Maximum buffer size/)).toBeNull();
  });

  it('renders the audio page controls', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    expect(screen.getByLabelText(/^Audio output while recording/)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add track' })).toBeTruthy();
  });

  it('renders the capture page controls, including the game-capture timeout', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
    expect(screen.getByLabelText(/^Capture method/)).toBeTruthy();
    expect(screen.getByLabelText(/^Game-capture timeout/)).toBeTruthy();
  });

  it('renders the game page controls, and no capture setting of its own', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Games' }));
    expect(screen.queryByLabelText(/^Game-capture timeout/)).toBeNull();
    expect(screen.getByText(/known games come from the project catalogue/i)).toBeTruthy();
    expect(screen.queryByLabelText(/^Capture mode/)).toBeNull();
  });

  it('correlates the custom-game executable picker command and response', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Games' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));

    const frame = JSON.parse(ws.sent.at(-1) ?? '{}') as {
      method?: string;
      parameters?: { requestId?: string };
    };
    expect(frame).toEqual({
      method: 'SelectGameExecutable',
      parameters: { requestId: expect.any(String) },
    });

    act(() => {
      ws.serverMessage(JSON.stringify({
        method: 'selectedGameExecutable',
        content: { requestId: 'stale-request', filePath: 'C:\\Wrong\\wrong.exe' },
      }));
    });
    expect((screen.getByLabelText('Executable path') as HTMLInputElement).value).toBe('');

    act(() => {
      ws.serverMessage(JSON.stringify({
        method: 'selectedGameExecutable',
        content: { requestId: frame.parameters?.requestId, filePath: 'C:\\Games\\Picked\\game.exe' },
      }));
    });
    expect((screen.getByLabelText('Executable path') as HTMLInputElement).value).toBe(
      'C:\\Games\\Picked\\game.exe',
    );
  });

  it('keeps a custom-game draft and validation response until its update is accepted', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Games' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.change(screen.getByLabelText('Find game'), { target: { value: 'Pending game' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    const searchFrame = JSON.parse(ws.sent.at(-1) ?? '{}') as {
      parameters?: { requestId?: string };
    };
    act(() => ws.serverMessage(JSON.stringify({
      method: 'gameSearchResults',
      content: {
        requestId: searchFrame.parameters?.requestId,
        results: [{ gameId: '01HPENDINGGAME0000000000000', name: 'Pending game', source: 'local' }],
      },
    })));
    fireEvent.click(screen.getByRole('option', { name: /Pending game/ }));
    fireEvent.change(screen.getByLabelText('Executable path'), {
      target: { value: 'C:\\Games\\Pending\\game.exe' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    const frame = JSON.parse(ws.sent.at(-1) ?? '{}') as {
      method?: string;
      parameters?: { requestId?: string };
    };
    expect(frame).toMatchObject({
      method: 'UpdateSettings',
      parameters: { requestId: expect.any(String) },
    });
    expect(screen.getByTestId('custom-game-draft')).toBeTruthy();

    act(() => ws.serverMessage(JSON.stringify({
      method: 'settingsUpdateResult',
      content: {
        requestId: frame.parameters?.requestId,
        success: false,
        error: 'Executable already belongs to another game.',
      },
    })));

    expect(screen.getByRole('option', { name: /Pending game/ }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('alert').textContent).toBe('Executable already belongs to another game.');
  });

  it('keeps the capture method on the Capture page', () => {
    renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
    expect(screen.getByLabelText(/^Capture method/)).toBeTruthy();
  });

  it('sends a partial settings object, not the whole settings, when a field is edited', () => {
    const { ws } = renderSettings();
    changeInput(/^Frame rate/, '144');
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { fps: 144 } });
  });

  it('resolution is a selector over the common sizes', () => {
    const { ws } = renderSettings();
    const select = screen.getByLabelText(/^Resolution/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
    ]);
    expect(select.value).toBe('1920x1080');
    expect(Array.from(select.options).map((option) => option.text)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
    ]);

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

    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      '1280x720',
      '1920x1080',
      '2560x1440',
      '3840x2160',
      '1600x900',
    ]);
    expect(select.options[4].text).toBe('1600x900 (custom)');
    expect(select.value).toBe('1600x900');
    expect(sentUpdates(ws)).toHaveLength(0);
  });

  it('frame rate is a selector over the common values, defaulting to 60', () => {
    const { ws } = renderSettings();
    const select = screen.getByLabelText(/^Frame rate/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual(['30', '60', '90', '144']);
    expect(select.value).toBe('60');

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

    fireEvent.change(select, { target: { value: 'ffmpeg_vaapi' } });
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { encoder: 'ffmpeg_vaapi' } });
  });

  it('encoder selector hides unsupported encoders but keeps the stored one selectable', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_nvenc_h264_tex';
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).toEqual([
      'obs_x264',
      'obs_nvenc_h264_tex',
    ]);
    expect(select.value).toBe('obs_nvenc_h264_tex');
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
    pushSettings(ws, makeSettings(), 'server:init', ['jim_nvenc', 'obs_nvenc_h264_tex', 'obs_x264']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    const labels = Array.from(select.options).map((option) => option.text);

    expect(labels).toContain('NVIDIA (NVENC) (jim_nvenc)');
    expect(labels).toContain('NVIDIA (NVENC) (obs_nvenc_h264_tex)');
    expect(labels).toContain('Software (x264)');
  });

  it('an unrecognised encoder id is offered under its own id rather than hidden', () => {
    const { ws } = renderSettings();
    pushSettings(ws, makeSettings(), 'server:init', ['obs_x264', 'some_future_h264_encoder']);
    const select = screen.getByLabelText(/^Encoder/) as HTMLSelectElement;

    expect(Array.from(select.options).map((option) => option.text)).toContain('some_future_h264_encoder');
  });

  it('encoder falls back to the stored value plus obs_x264 when the list is unknown', () => {
    const { ws } = renderSettings();
    const select = () => screen.getByLabelText(/^Encoder/) as HTMLSelectElement;
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['x264', 'obs_x264']);
    expect(select().value).toBe('x264');

    pushSettings(ws, makeSettings(), 'server:init', null);
    expect(Array.from(select().options).map((option) => option.value)).toEqual(['x264', 'obs_x264']);
    expect(select().value).toBe('x264');
  });

  it('rate control offers only the modes the selected encoder supports', () => {
    const withEncoder = makeSettings();
    withEncoder.recording.encoder = 'obs_x264';
    const { ws } = renderSettings();
    pushSettings(ws, withEncoder, 'server:init', ['obs_x264', 'ffmpeg_vaapi']);
    const select = () => screen.getByLabelText(/^Rate control/) as HTMLSelectElement;

    expect(Array.from(select().options).map((option) => option.value)).toEqual(['Crf', 'Cbr', 'Vbr']);
    expect(Array.from(select().options).map((option) => option.text)).toEqual([
      'Constant quality (CRF)',
      'Constant bitrate (CBR)',
      'Variable bitrate (VBR)',
    ]);

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
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
    expect(screen.queryByLabelText(/^Bitrate/)).toBeNull();
    expect(screen.queryByLabelText(/^Maximum bitrate/)).toBeNull();

    const cbr = makeSettings();
    cbr.recording.rateControl = 'Cbr';
    pushSettings(ws, cbr, 'server:init');
    expect(screen.queryByLabelText(/^Quality/)).toBeNull();
    expect((screen.getByLabelText(/^Bitrate/) as HTMLInputElement).value).toBe('15000');
    expect(screen.queryByLabelText(/^Maximum bitrate/)).toBeNull();
  });

  it('keeps a custom quality value visible instead of rendering a blank select', () => {
    const custom = makeSettings();
    custom.recording.quality = 7;
    const { ws } = renderSettings();
    pushSettings(ws, custom, 'server:init');

    const quality = screen.getByLabelText(/^Quality/) as HTMLSelectElement;
    expect(quality.value).toBe('7');
    expect(quality.selectedOptions[0].text).toBe('7 (custom)');
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
    expect((screen.getByLabelText(/^Bitrate/) as HTMLInputElement).value).toBe('15000');
  });

  it('a stored mode the encoder cannot use is shown coerced, and nothing is sent', () => {
    const { ws } = renderSettings();
    const carried = makeSettings();
    carried.recording.encoder = 'ffmpeg_vaapi';
    carried.recording.rateControl = 'Crf';
    pushSettings(ws, carried, 'server:init', ['ffmpeg_vaapi']);

    const select = screen.getByLabelText(/^Rate control/) as HTMLSelectElement;
    expect(select.value).toBe('Cqp');
    expect(Array.from(select.options).map((option) => option.value)).not.toContain('Crf');
    expect(sentUpdates(ws)).toHaveLength(0);
    expect(screen.getByText(/does not support it/)).toBeTruthy();
  });

  it('rate control renders from the defaults when the backend sends no mode at all', () => {
    const { ws } = renderSettings();
    const older = makeSettings();
    delete older.recording.rateControl;
    delete older.recording.bitrateKbps;
    pushSettings(ws, older, 'server:init');

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

    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    const browseFrame = JSON.parse(ws.sent[ws.sent.length - 1]);
    expect(browseFrame).toEqual({ method: 'SetVideoLocation' });

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

  it('audio page: applies live endpoint level messages', () => {
    const { ws } = renderSettings();
    const settings = makeSettings();
    settings.audio.tracks = [{
      id: 'playback',
      name: 'Playback',
      sources: [{ name: 'Speakers', kind: 'Output', deviceId: 'speaker-1', volume: 1 }],
    }];
    pushSettings(ws, settings);
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));

    act(() => {
      ws.serverMessage(JSON.stringify({
        method: 'audioLevels',
        content: { levels: [{ deviceId: 'speaker-1', peak: 0.37 }] },
      }));
    });

    expect(screen.getByRole('meter', { name: 'Audio level for Speakers' }).getAttribute('aria-valuenow')).toBe('37');
  });

  it('asks for audio levels only while the audio page is showing', () => {
    const watches = (ws: MockWebSocket) => ws.sent
      .map((frame) => JSON.parse(frame) as { method?: string })
      .filter((frame) => frame.method === 'WatchAudioLevels').length;
    const { ws } = renderSettings();
    expect(watches(ws)).toBe(0);

    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    expect(watches(ws)).toBe(1);

    fireEvent.click(screen.getByRole('tab', { name: 'General' }));
    const afterLeaving = watches(ws);
    act(() => {
      ws.serverMessage(JSON.stringify({
        method: 'audioLevels',
        content: { levels: [{ deviceId: 'speaker-1', peak: 0.5 }] },
      }));
    });
    expect(watches(ws)).toBe(afterLeaving);
  });

  it('does not ask for audio levels while inactive, even on the audio page', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    const view = (active: boolean) => (
      <ToastProvider><SettingsView client={client} active={active} /></ToastProvider>
    );
    const { rerender } = render(view(false));
    client.connect();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
    });
    pushSettings(ws);
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));

    const watchCount = () => ws.sent.filter((frame) => frame.includes('"WatchAudioLevels"')).length;
    expect(watchCount()).toBe(0);

    rerender(view(true));
    expect(watchCount()).toBe(1);
  });

  it('audio page: assigning a source to a track routes it with default volume 1', () => {
    const { ws } = renderSettings();
    fireEvent.click(screen.getByRole('tab', { name: 'Audio' }));
    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));
    const sent1 = sentUpdates(ws);
    const tracks1 = (sent1[0] as { audio: { tracks: AudioTrackLike[] } }).audio.tracks;
    expect(tracks1).toHaveLength(1);

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
    changeInput(/^Frame rate/, '144');
    expect(sentUpdates(ws)).toHaveLength(1);

    const echo = makeSettings();
    echo.recording.fps = 144;
    pushSettings(ws, echo, 'tript:recording:1');
    expect(frameRate().value).toBe('144');

    const external = makeSettings();
    external.recording.fps = 30;
    pushSettings(ws, external, 'otherProcess:init');
    expect(frameRate().value).toBe('30');
  });

  it('reads from the settings message, not a stale shadow copy', () => {
    const { ws } = renderSettings();
    const updated = makeSettings();
    updated.recording.mode = 'Session';
    pushSettings(ws, updated, 'server:changed');
    fireEvent.click(screen.getByRole('tab', { name: 'Recording' }));
    expect((screen.getByLabelText(/^Recording mode/) as HTMLSelectElement).value).toBe('Session');
  });

  it('shows a loading state instead of the form before the backend pushes settings', () => {
    const { factory } = createMockSocketFactory();
    const client = createIpcClient({ createSocket: factory });
    render(<ToastProvider><SettingsView client={client} /></ToastProvider>);
    fireEvent.click(screen.getByRole('tab', { name: 'Recording' }));

    expect(screen.getByText('Loading settings…')).toBeTruthy();
    expect(screen.queryByLabelText(/^Frame rate/)).toBeNull();

    client.connect();
    const ws = activeSocket();
    act(() => {
      ws.serverOpen();
    });
    pushSettings(ws);

    expect(screen.queryByText('Loading settings…')).toBeNull();
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

const MONITORS: DisplayInfo[] = [
  { id: 'monitor-1', name: 'DP-1', width: 2560, height: 1440, primary: true },
  { id: 'monitor-2', name: 'HDMI-A-1', width: 1920, height: 1080, primary: false },
];

function renderCapture(
  capture: CaptureSettings,
  availableDisplays?: DisplayInfo[] | null,
): { ws: MockWebSocket } {
  const { factory } = createMockSocketFactory();
  const client = createIpcClient({ createSocket: factory });
  render(<ToastProvider><SettingsView client={client} /></ToastProvider>);
  client.connect();
  const ws = activeSocket();
  act(() => {
    ws.serverOpen();
  });
  const settings = makeSettings();
  settings.capture = capture;
  pushSettings(ws, settings, undefined, undefined, undefined, availableDisplays);
  fireEvent.click(screen.getByRole('tab', { name: 'Capture' }));
  return { ws };
}

function displaySelect(): HTMLSelectElement {
  return screen.getByTestId('capture-display-select') as HTMLSelectElement;
}

describe('SettingsView capture page — monitor selection', () => {
  afterEach(() => {
    cleanup();
  });

  it('offers the attached monitors, labelled with their size and the primary marked', () => {
    renderCapture({ method: 'Display', display: null, displayLabel: null }, MONITORS);
    expect(Array.from(displaySelect().options).map((option) => option.text)).toEqual([
      'Primary monitor (automatic)',
      'DP-1: 2560x1440 (primary)',
      'HDMI-A-1: 1920x1080',
    ]);
  });

  it('shows the picker under Auto as well, and hides it for Game', () => {
    renderCapture({ method: 'Auto', display: null, displayLabel: null }, MONITORS);
    expect(displaySelect()).toBeTruthy();
    cleanup();
    renderCapture({ method: 'Game', display: null, displayLabel: null }, MONITORS);
    expect(screen.queryByTestId('capture-display-select')).toBeNull();
    expect(screen.queryByTestId('capture-display-input')).toBeNull();
  });

  it('sends the id and the name in one patch when a monitor is picked', () => {
    const { ws } = renderCapture({ method: 'Display', display: null, displayLabel: null }, MONITORS);
    fireEvent.change(displaySelect(), { target: { value: 'monitor-2' } });
    expect(sentUpdates(ws)).toEqual([
      { capture: { display: 'monitor-2', displayLabel: 'HDMI-A-1' } },
    ]);
  });

  it('clears both fields when the primary sentinel is picked', () => {
    const { ws } = renderCapture(
      { method: 'Display', display: 'monitor-2', displayLabel: 'HDMI-A-1' },
      MONITORS,
    );
    fireEvent.change(displaySelect(), { target: { value: '__primary_display__' } });
    expect(sentUpdates(ws)).toEqual([{ capture: { display: null, displayLabel: null } }]);
  });

  it('keeps a monitor that is no longer attached selected, marked "(not connected)"', () => {
    renderCapture({ method: 'Display', display: 'monitor-gone', displayLabel: 'DP-3' }, MONITORS);
    const select = displaySelect();
    expect(select.value).toBe('monitor-gone');
    expect(select.selectedOptions[0].text).toBe('DP-3 (not connected)');
  });

  it('degrades to a typed identifier when the host could not enumerate (null)', () => {
    const { ws } = renderCapture({ method: 'Display', display: 'DP-1', displayLabel: null }, null);
    const input = screen.getByTestId('capture-display-input') as HTMLInputElement;
    expect(input.value).toBe('DP-1');
    fireEvent.change(input, { target: { value: 'DP-2' } });
    expect(sentUpdates(ws)).toEqual([{ capture: { display: 'DP-2', displayLabel: null } }]);
  });

  it('says so instead of showing an empty dropdown when nothing was found ([])', () => {
    renderCapture({ method: 'Display', display: 'monitor-gone', displayLabel: 'DP-3' }, []);
    expect(screen.queryByTestId('capture-display-select')).toBeNull();
    expect(screen.queryByTestId('capture-display-input')).toBeNull();
    const note = screen.getByTestId('capture-display-none').textContent ?? '';
    expect(note).toContain('No monitors were detected');
    expect(note).toContain('DP-3 (not connected)');
  });
});
