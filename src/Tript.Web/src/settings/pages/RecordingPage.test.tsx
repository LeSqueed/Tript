// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { RecordingPage } from './RecordingPage';
import type { BufferSettings, RecordingSettings } from '../settingsModel';

const SETTINGS: RecordingSettings = {
  mode: 'SessionWithReplayBuffer',
  resolutionWidth: 1920,
  resolutionHeight: 1080,
  fps: 60,
  encoder: 'x264',
  quality: 10,
  rateControl: 'Cqp',
  bitrateKbps: 15000,
};

const BUFFER: BufferSettings = {
  enabled: false,
  duration: 30,
  maxSizeBytes: 4 * 1024 * 1024 * 1024,
};

function renderPage(settings: RecordingSettings = SETTINGS, buffer: BufferSettings = BUFFER) {
  const update = vi.fn();
  render(
    <RecordingPage
      settings={settings}
      buffer={buffer}
      update={update}
      page="recording"
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

describe('recording page layout', () => {
  it('renders the main fields, with quality in the default constant-quality mode', () => {
    renderPage();

    expect(screen.getByLabelText(/^Recording mode/)).toBeTruthy();
    expect(screen.getByLabelText(/^Resolution/)).toBeTruthy();
    expect(screen.getByLabelText(/^Frame rate/)).toBeTruthy();
    expect(screen.getByLabelText(/^Quality/)).toBeTruthy();
    expect(screen.getByLabelText(/^HDR/)).toBeTruthy();
    expect(screen.queryByLabelText(/^Bitrate/)).toBeNull();
  });

  it('keeps the encoder and rate control in the advanced disclosure', () => {
    renderPage();

    expect(screen.getByText('Encoder and bitrate')).toBeTruthy();
    expect(screen.getByLabelText(/^Encoder/)).toBeTruthy();
    expect(screen.getByLabelText(/^Rate control/)).toBeTruthy();
    expect(screen.getByLabelText(/^Maximum buffer size/)).toBeTruthy();
  });

  it('keeps a custom stored quality visible', () => {
    renderPage({ ...SETTINGS, quality: 7 });
    const quality = screen.getByLabelText(/^Quality/) as HTMLSelectElement;
    expect(quality.value).toBe('7');
    expect(quality.selectedOptions[0].text).toBe('7 (custom)');
  });

  it('no longer renders the removed automatic-highlight controls', () => {
    renderPage();

    expect(screen.queryByLabelText(/^Delete linked highlights by default/)).toBeNull();
    expect(screen.queryByLabelText(/^Seconds before each highlight/)).toBeNull();
    expect(screen.queryByLabelText(/^Seconds after each highlight/)).toBeNull();
  });
});

describe('recording mode change', () => {
  it('offers replay-buffer-only mode and sends it unchanged', () => {
    const update = renderPage();
    const mode = screen.getByLabelText(/^Recording mode/) as HTMLSelectElement;

    expect(Array.from(mode.options).map((option) => option.text)).toContain(
      'Replay buffer only (highlights without session recordings)',
    );
    fireEvent.change(mode, { target: { value: 'ReplayBufferOnly' } });
    expect(update).toHaveBeenCalledWith('recording', { mode: 'ReplayBufferOnly' });
  });

  it('sends just the mode when switching to Session with automatic highlights off', () => {
    const update = renderPage({ ...SETTINGS, mode: 'SessionWithReplayBuffer' });
    fireEvent.change(screen.getByLabelText(/^Recording mode/), { target: { value: 'Session' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('recording', { mode: 'Session' });
  });

  it('asks for confirmation before switching to Session with automatic highlights on', () => {
    const update = renderPage({ ...SETTINGS, automaticClipsEnabled: true });
    fireEvent.change(screen.getByLabelText(/^Recording mode/), { target: { value: 'Session' } });

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(update).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Turn buffer off' }));
    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('recording', { mode: 'Session', automaticClipsEnabled: false });
  });

  it('sends nothing when the buffer-off confirmation is cancelled', () => {
    const update = renderPage({ ...SETTINGS, automaticClipsEnabled: true });
    fireEvent.change(screen.getByLabelText(/^Recording mode/), { target: { value: 'Session' } });
    expect(screen.getByRole('dialog')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Keep buffer on' }));
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(update).not.toHaveBeenCalled();
  });

  it('uses a modal focus trap, Escape cancellation, and non-submit action buttons', () => {
    renderPage({ ...SETTINGS, automaticClipsEnabled: true });
    const mode = screen.getByLabelText(/^Recording mode/) as HTMLSelectElement;
    mode.focus();
    fireEvent.change(mode, { target: { value: 'Session' } });

    const dialog = screen.getByRole('dialog');
    const cancel = screen.getByRole('button', { name: 'Keep buffer on' }) as HTMLButtonElement;
    const confirm = screen.getByRole('button', { name: 'Turn buffer off' }) as HTMLButtonElement;
    expect(dialog.parentElement).toBe(document.body);
    expect(cancel.type).toBe('button');
    expect(confirm.type).toBe('button');
    expect(document.activeElement).toBe(cancel);

    confirm.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(cancel);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(document.activeElement).toBe(mode);
  });
});

describe('maximum buffer size', () => {
  it('commits the buffer size under the buffer page in bytes', () => {
    const update = renderPage();
    expect((screen.getByLabelText(/^Maximum buffer size/) as HTMLInputElement).type).toBe('number');
    changeInput(/^Maximum buffer size/, '2048');

    expect(update).toHaveBeenCalledWith('buffer', { maxSizeBytes: 2048 * 1024 * 1024 });
  });

  it('reverts a non-numeric buffer size draft rather than sending it', () => {
    const update = renderPage();
    changeInput(/^Maximum buffer size/, 'lots');

    expect(update).not.toHaveBeenCalled();
    expect((screen.getByLabelText(/^Maximum buffer size/) as HTMLInputElement).value).toBe('4096');
  });

  it('preserves newer typing across a self echo and resyncs on an external push', () => {
    const update = vi.fn();
    const view = (buffer: BufferSettings, externalPushCount: number) => (
      <RecordingPage settings={SETTINGS} buffer={buffer} update={update} page="recording"
        externalPushCount={externalPushCount} availableEncoders={undefined}
        displayResolution={undefined} />
    );
    const { rerender } = render(view(BUFFER, 0));
    const input = screen.getByLabelText(/^Maximum buffer size/) as HTMLInputElement;
    fireEvent.change(input, { target: { value: '2048' } });

    rerender(view({ ...BUFFER, maxSizeBytes: 1024 * 1024 * 1024 }, 0));
    expect(input.value).toBe('2048');

    rerender(view({ ...BUFFER, maxSizeBytes: 512 * 1024 * 1024 }, 1));
    expect(input.value).toBe('512');
  });
});
