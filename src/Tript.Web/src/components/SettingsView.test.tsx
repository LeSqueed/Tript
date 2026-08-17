// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings page tests: each page renders its controls, editing a field sends the right
// UpdateSettings partial, the audio routing model behaves (assigning a source to a track,
// per-source volume, two sources on one track), and the cause-echo discipline holds (an echo of
// our own cause does not clobber an in-progress edit). The IPC client is exercised over a real
// IpcClient bound to a mock socket, so the wire shape is asserted on the sent frames.

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { render, screen, fireEvent, cleanup, act } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import { SettingsView } from './SettingsView';
import type { AudioSourceKind, SettingsMessageContent } from '../settings/settingsModel';

/** The active socket — the app's live socket is last under StrictMode's double effect. */
function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

/** The full settings object pushed by the backend. */
function makeSettings(): SettingsMessageContent['settings'] {
  return {
    recording: { mode: 'Hybrid', resolutionWidth: 1920, resolutionHeight: 1080, fps: 60, encoder: 'x264', quality: 10, outputDirectory: null },
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

function pushSettings(ws: MockWebSocket, settings = makeSettings(), cause?: string) {
  const content: SettingsMessageContent = { settings };
  if (cause !== undefined) {
    content.cause = cause;
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

/** Set a number input's value (React's controlled-input quirk: fire change then blur). */
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
    expect(screen.getByLabelText(/^Frame rate/)).toBeTruthy();
    expect(screen.getByLabelText(/^Encoder/)).toBeTruthy();
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

  it('a push with our own cause does not clobber an in-progress edit', () => {
    const { ws } = renderSettings();
    // The user edits the frame rate; the send is tagged with our cause.
    changeInput(/^Frame rate/, '144');
    expect(sentUpdates(ws)).toHaveLength(1);

    // The backend echoes the full model with the same cause (its persistence has the new value).
    const echo = makeSettings();
    echo.recording.fps = 144;
    pushSettings(ws, echo, 'tript:recording:1');

    // The UI must NOT have been re-rendered from the echo (it already shows the user's value).
    expect((screen.getByLabelText(/^Frame rate/) as HTMLInputElement).value).toBe('144');

    // A push with a foreign cause is a real external change and replaces the model.
    const external = makeSettings();
    external.recording.fps = 30;
    pushSettings(ws, external, 'otherProcess:init');
    expect((screen.getByLabelText(/^Frame rate/) as HTMLInputElement).value).toBe('30');
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
    changeInput(/^Frame rate/, '120');
    const sent = sentUpdates(ws);
    expect(sent).toHaveLength(1);
    expect(sent[0]).toEqual({ recording: { fps: 120 } });
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
