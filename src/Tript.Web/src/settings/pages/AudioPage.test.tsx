// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the audio page: the plain default up front, the advanced disclosure carrying the
// output mode, the source kind pills, and the track routing.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { AudioPage, buildSourceOptions } from './AudioPage';
import type { AudioDeviceSetting, AudioSettings, AudioTrack } from '../settingsModel';

const DEVICES: AudioDeviceSetting[] = [
  { id: 'dev-mic', name: 'Headset Mic', direction: 'Input' },
  { id: 'dev-out', name: 'Headphones', direction: 'Output' },
];

function makeSettings(overrides?: Partial<AudioSettings>): AudioSettings {
  return {
    outputMode: 'Normal',
    tracks: [],
    devices: DEVICES,
    mic: null,
    desktop: null,
    ...overrides,
  };
}

function renderPage(settings: AudioSettings = makeSettings(), levels?: Readonly<Record<string, number>>) {
  const update = vi.fn();
  render(<AudioPage settings={settings} levels={levels} update={update} page="audio" />);
  return update;
}

afterEach(cleanup);

describe('audio page layout', () => {
  it('renders the default-behavior note at the top of the page', () => {
    renderPage();

    expect(
      screen.getByText(/With no custom tracks, Tript records the audio in OBS's programme mix/),
    ).toBeTruthy();
    expect(screen.queryByText(/records your microphone/i)).toBeNull();
  });

  it('renders with no tracks and a couple of devices', () => {
    renderPage(makeSettings());

    expect(screen.getByText('Tracks')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add track' })).toBeTruthy();
    expect(screen.queryByLabelText(/^Audio output while recording/)).toBeTruthy();
  });
});

describe('advanced disclosure', () => {
  it('holds the output-mode select, and choosing Mute patches the audio page', () => {
    const update = renderPage();

    expect(screen.getByText('Advanced')).toBeTruthy();
    const select = screen.getByLabelText(/^Audio output while recording/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent)).toEqual([
      'Normal (hear it as usual)',
      'Mute (silent while recording)',
      'Disable (off entirely while recording)',
    ]);

    fireEvent.change(select, { target: { value: 'Mute' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('audio', { outputMode: 'Mute' });
  });
});

describe('source kind pills', () => {
  it('read Mic and Playback, with the WASAPI endpoint in the title', () => {
    const track: AudioTrack = {
      id: 't1',
      name: 'Track 1',
      sources: [
        { name: 'Microphone', kind: 'Input', label: 'Microphone', sourceKey: 'mic', volume: 1 },
        { name: 'System output', kind: 'Output', label: 'System output', sourceKey: 'system', volume: 1 },
      ],
    };
    renderPage(makeSettings({ tracks: [track] }));

    const micPill = screen.getByText('Mic') as HTMLElement;
    const playbackPill = screen.getByText('Playback') as HTMLElement;
    expect(micPill.title).toBe('WASAPI capture endpoint (input)');
    expect(playbackPill.title).toBe('WASAPI render endpoint (output)');
  });
});

describe('device levels', () => {
  it('renders and clamps the live level for a selected endpoint', () => {
    const track: AudioTrack = {
      id: 't1',
      name: 'Playback',
      sources: [{ name: 'Headphones', kind: 'Output', deviceId: 'dev-out', volume: 1 }],
    };
    renderPage(makeSettings({ tracks: [track] }), { 'dev-out': 1.4 });

    const meter = screen.getByRole('meter', { name: 'Audio level for Headphones' });
    expect(meter.getAttribute('aria-valuenow')).toBe('100');
    expect(meter.querySelector('.audio-source-meter-fill')?.getAttribute('style')).toContain('scaleX(1)');
  });
});

describe('track routing', () => {
  it('sorts discovered devices by name after the built-in sources', () => {
    expect(buildSourceOptions([
      { id: 'z-device', name: 'Zeta speakers', direction: 'Output' },
      { id: 'a-device', name: 'alpha microphone', direction: 'Input' },
      { id: 'b-device', name: 'Bravo headset', direction: 'Output' },
    ]).map((option) => option.label)).toEqual([
      'Microphone',
      'System output',
      'Game audio',
      'alpha microphone',
      'Bravo headset',
      'Zeta speakers',
    ]);
  });

  it('offers the same device on another track but not twice on its current track', () => {
    const assigned = { name: 'Headset Mic', kind: 'Input' as const, deviceId: 'dev-mic', volume: 0.8 };
    renderPage(makeSettings({
      tracks: [
        { id: 't1', name: 'Mic', sources: [assigned] },
        { id: 't2', name: 'Mixed', sources: [] },
      ],
    }));

    const selects = screen.getAllByLabelText('Source to add to this track') as HTMLSelectElement[];
    expect(Array.from(selects[0].options).map((option) => option.value)).not.toContain('dev-mic');
    expect(Array.from(selects[1].options).map((option) => option.value)).toContain('dev-mic');
  });

  it('recognizes a persisted built-in source without frontend-only identity fields', () => {
    renderPage(makeSettings({
      tracks: [{
        id: 't1',
        name: 'Mic',
        sources: [{ name: 'Microphone', kind: 'Input', volume: 1 }],
      }],
    }));

    const select = screen.getByLabelText('Source to add to this track') as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.value)).not.toContain('mic');
  });

  it('sends the full tracks array when a track is added', () => {
    const track: AudioTrack = { id: 't1', name: 'Track 1', sources: [] };
    const update = renderPage(makeSettings({ tracks: [track] }));

    fireEvent.click(screen.getByRole('button', { name: 'Add track' }));

    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][0]).toBe('audio');
    const tracks = update.mock.calls[0][1].tracks as AudioTrack[];
    expect(tracks).toHaveLength(2);
    expect(tracks[0]).toEqual(track);
    expect(tracks[1]).toMatchObject({ name: 'Track 2', sources: [] });
  });
});
