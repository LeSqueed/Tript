// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { GeneralPage } from './GeneralPage';
import type { GeneralSettings, RecordingSettings } from '../settingsModel';

const GENERAL: GeneralSettings = {
  startWithWindows: false,
  startupVisibility: 'Window',
  minimizeBehavior: 'Taskbar',
  closeBehavior: 'Exit',
  notifications: {
    enabled: true,
    recordingStarted: true,
    recordingStopped: true,
    errors: true,
    recovery: true,
  },
};

const RECORDING: RecordingSettings = {
  mode: 'SessionWithReplayBuffer',
  resolutionWidth: 1920,
  resolutionHeight: 1080,
  fps: 60,
  encoder: 'x264',
  quality: 10,
  trashRetentionHours: 24,
};

function renderPage(
  general: GeneralSettings = GENERAL,
  recording: RecordingSettings = RECORDING,
) {
  const update = vi.fn();
  render(
    <GeneralPage
      settings={general}
      recording={recording}
      update={update}
      page="general"
    />,
  );
  return update;
}

function trashRetention(): HTMLSelectElement {
  return screen.getByLabelText('Empty the trash after') as HTMLSelectElement;
}

function deletionDefault(): HTMLInputElement {
  return screen.getByLabelText(/^Delete linked highlights by default/) as HTMLInputElement;
}

afterEach(cleanup);

describe('trash retention', () => {
  it('renders the stored value in the select', () => {
    renderPage();
    expect(trashRetention().value).toBe('24');
  });

  it('sends a recording page patch with the chosen retention', () => {
    const update = renderPage();
    fireEvent.change(trashRetention(), { target: { value: '168' } });

    expect(update).toHaveBeenCalledWith('recording', { trashRetentionHours: 168 });
  });

  it('shows an unusual stored value via an appended current-value option', () => {
    renderPage(GENERAL, { ...RECORDING, trashRetentionHours: 100 });

    expect(trashRetention().value).toBe('100');
    expect(screen.getByText('100 hours (current)')).toBeTruthy();
  });

  it('shows legacy negative and zero retention values as Never', () => {
    const { rerender } = render(
      <GeneralPage settings={GENERAL} recording={{ ...RECORDING, trashRetentionHours: -5 }}
        update={vi.fn()} page="general" />,
    );
    expect(trashRetention().value).toBe('0');
    expect(screen.queryByText('-5 hours (current)')).toBeNull();

    rerender(
      <GeneralPage settings={GENERAL} recording={{ ...RECORDING, trashRetentionHours: 0 }}
        update={vi.fn()} page="general" />,
    );
    expect(trashRetention().selectedOptions[0].text).toBe('Never');
  });
});

describe('linked-highlight deletion default', () => {
  it('renders an absent value unchecked', () => {
    renderPage(GENERAL, { ...RECORDING, deleteLinkedHighlightsByDefault: undefined });
    expect(deletionDefault().checked).toBe(false);
  });

  it('updates only deleteLinkedHighlightsByDefault to true', () => {
    const update = renderPage();
    fireEvent.click(deletionDefault());

    expect(update).toHaveBeenCalledWith('recording', { deleteLinkedHighlightsByDefault: true });
  });
});

describe('HDR clip conversion', () => {
  it('shows its descriptor in the standard field hint', () => {
    renderPage();

    const hint = screen.getByText('New clips made from HDR footage are converted for players and displays that expect SDR. Original recordings stay as they were.');
    expect(hint.className).toBe('field-hint');
  });

  it('sends a general page patch on toggle', () => {
    const update = renderPage();
    fireEvent.click(screen.getByLabelText('Convert HDR clips to SDR'));

    expect(update).toHaveBeenCalledWith('general', { convertHdrClipsToSdr: true });
  });

  it('sends a general page patch back to false', () => {
    const update = renderPage({ ...GENERAL, convertHdrClipsToSdr: true });
    fireEvent.click(screen.getByLabelText('Convert HDR clips to SDR'));

    expect(update).toHaveBeenCalledWith('general', { convertHdrClipsToSdr: false });
  });
});
