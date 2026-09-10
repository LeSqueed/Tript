// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { HighlightsPage } from './HighlightsPage';
import type { BufferSettings, RecordingSettings } from '../settingsModel';

const BUFFER: BufferSettings = {
  enabled: false,
  duration: 30,
  maxSizeBytes: 4 * 1024 * 1024 * 1024,
};

const BUFFER_MODE: RecordingSettings = {
  mode: 'SessionWithReplayBuffer',
  resolutionWidth: 1920,
  resolutionHeight: 1080,
  fps: 60,
  encoder: 'x264',
  quality: 10,
  automaticClipsEnabled: false,
  automaticClipBeforeSeconds: 5,
  automaticClipAfterSeconds: 8,
};

const SESSION_MODE: RecordingSettings = { ...BUFFER_MODE, mode: 'Session' };
const BUFFER_ONLY_MODE: RecordingSettings = { ...BUFFER_MODE, mode: 'ReplayBufferOnly' };

function renderPage(recording: RecordingSettings = BUFFER_MODE, buffer: BufferSettings = BUFFER) {
  const update = vi.fn();
  render(
    <HighlightsPage
      settings={buffer}
      recording={recording}
      update={update}
      page="buffer"
      externalPushCount={0}
    />,
  );
  return update;
}

function changeInput(label: RegExp, value: string) {
  const input = screen.getByLabelText(label);
  fireEvent.change(input, { target: { value } });
  fireEvent.blur(input);
}

afterEach(cleanup);

describe('highlights page in buffer mode', () => {
  it('commits buffer fields under the buffer page and clip fields under the recording page', () => {
    const update = renderPage();

    expect((screen.getByLabelText(/^Automatic highlights/) as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText(/^Seconds before each highlight/) as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText(/^Seconds after each highlight/) as HTMLInputElement).disabled).toBe(false);

    changeInput(/^Buffer length/, '45');
    expect(update).toHaveBeenCalledWith('buffer', { duration: 45 });

    fireEvent.click(screen.getByLabelText(/^Automatic highlights/));
    expect(update).toHaveBeenCalledWith('recording', { automaticClipsEnabled: true });
  });

  it('uses shared numeric text fields for all three numeric settings', () => {
    renderPage();

    expect((screen.getByLabelText(/^Buffer length/) as HTMLInputElement).type).toBe('number');
    expect((screen.getByLabelText(/^Seconds before/) as HTMLInputElement).type).toBe('number');
    expect((screen.getByLabelText(/^Seconds after/) as HTMLInputElement).type).toBe('number');
  });

  it('raises after along with before in one patch when before goes above after', () => {
    const update = renderPage();
    changeInput(/^Seconds before each highlight/, '12');

    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('recording', {
      automaticClipBeforeSeconds: 12,
      automaticClipAfterSeconds: 12,
    });
  });

  it('enables replay and automatic-highlight controls in replay-buffer-only mode', () => {
    renderPage(BUFFER_ONLY_MODE);

    expect((screen.getByLabelText(/^Automatic highlights/) as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText(/^Seconds before each highlight/) as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText(/^Seconds after each highlight/) as HTMLInputElement).disabled).toBe(false);
  });
});

describe('highlights page outside buffer mode', () => {
  it('disables the clip controls and points at the recording tab', () => {
    renderPage(SESSION_MODE);

    expect((screen.getByLabelText(/^Automatic highlights/) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText(/^Seconds before each highlight/) as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText(/^Seconds after each highlight/) as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(/Needs a replay buffer recording mode/i)).toBeTruthy();
  });

  it('lets the user clear a stale enabled setting while explaining that it is inactive', () => {
    const update = renderPage({ ...SESSION_MODE, automaticClipsEnabled: true });
    const toggle = screen.getByLabelText(/^Automatic highlights/) as HTMLInputElement;

    expect(toggle.checked).toBe(true);
    expect(toggle.disabled).toBe(false);
    expect(screen.getByText(/Inactive without a replay buffer recording mode/i)).toBeTruthy();
    fireEvent.click(toggle);
    expect(update).toHaveBeenCalledWith('recording', { automaticClipsEnabled: false });
  });
});

describe('reset', () => {
  it('resets the buffer defaults in one patch', () => {
    const update = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Reset to defaults' }));

    expect(update).toHaveBeenCalledWith('buffer', { duration: 30, maxSizeBytes: 4 * 1024 * 1024 * 1024 });
  });
});
