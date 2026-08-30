// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { RecordingPage } from './RecordingPage';
import type { RecordingSettings } from '../settingsModel';

const SETTINGS: RecordingSettings = {
  mode: 'SessionWithReplayBuffer',
  resolutionWidth: 1920,
  resolutionHeight: 1080,
  fps: 60,
  encoder: 'x264',
  quality: 10,
};

function renderPage(settings: RecordingSettings = SETTINGS) {
  const update = vi.fn();
  render(
    <RecordingPage
      settings={settings}
      update={update}
      page="recording"
      externalPushCount={0}
      onBrowse={vi.fn()}
    />,
  );
  return update;
}

function deletionDefault(): HTMLInputElement {
  return screen.getByLabelText(/^Delete linked highlights by default/) as HTMLInputElement;
}

afterEach(cleanup);

describe('linked-highlight deletion default', () => {
  it('renders an absent value unchecked and explains the deletion behavior', () => {
    renderPage();

    expect(deletionDefault().checked).toBe(false);
    expect(screen.getByText(/preselects the option to delete linked highlights/i)).toBeTruthy();
    expect(screen.getByText(/favourited highlights are always kept/i)).toBeTruthy();
  });

  it('updates only deleteLinkedHighlightsByDefault to true', () => {
    const update = renderPage();
    fireEvent.click(deletionDefault());

    expect(update).toHaveBeenCalledWith('recording', { deleteLinkedHighlightsByDefault: true });
  });

  it('updates only deleteLinkedHighlightsByDefault to false', () => {
    const update = renderPage({ ...SETTINGS, deleteLinkedHighlightsByDefault: true });
    expect(deletionDefault().checked).toBe(true);
    fireEvent.click(deletionDefault());

    expect(update).toHaveBeenCalledWith('recording', { deleteLinkedHighlightsByDefault: false });
  });
});
