// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { StoragePage } from './StoragePage';
import type { RecordingSettings, StorageSettings } from '../settingsModel';
import type { StorageReportMessage, StorageStatusMessage } from '../../ipc/protocol';

const GIGABYTE = 1024 * 1024 * 1024;

function settings(patch: Partial<StorageSettings> = {}): StorageSettings {
  return {
    minimumFreeBytes: 20 * GIGABYTE,
    whenFull: 'PauseRecording',
    policyConfirmed: false,
    keepSharingWhenFull: true,
    ...patch,
  };
}

function status(patch: Partial<StorageStatusMessage> = {}): StorageStatusMessage {
  return {
    pressure: 'ok',
    freeBytes: 40 * GIGABYTE,
    totalBytes: 100 * GIGABYTE,
    minimumFreeBytes: 20 * GIGABYTE,
    warnFreeBytes: 60 * GIGABYTE,
    recordingBlocked: false,
    policyConfirmed: false,
    whenFull: 'PauseRecording',
    keepSharingWhenFull: true,
    volumeRoot: 'T:\\',
    root: 'T:\\Tript',
    scratchFreeBytes: 0,
    scratchLow: false,
    ...patch,
  };
}

function report(patch: Partial<StorageReportMessage> = {}): StorageReportMessage {
  return {
    root: 'T:\\Tript',
    volumeRoot: 'T:\\',
    volumeTotalBytes: 100 * GIGABYTE,
    volumeFreeBytes: 40 * GIGABYTE,
    libraryBytes: 50 * GIGABYTE,
    sessionBytes: 40 * GIGABYTE,
    highlightBytes: 6 * GIGABYTE,
    clipBytes: 2 * GIGABYTE,
    trashBytes: 1 * GIGABYTE,
    sidecarBytes: 1 * GIGABYTE,
    favoriteBytes: 3 * GIGABYTE,
    sessionCount: 4,
    highlightCount: 9,
    clipCount: 2,
    trashCount: 1,
    games: [
      { gameId: 'ow', name: 'Overwatch', totalBytes: 30 * GIGABYTE, sessionBytes: 25 * GIGABYTE, highlightBytes: 4 * GIGABYTE, clipBytes: 1 * GIGABYTE },
      { gameId: 'val', name: 'Valorant', totalBytes: 18 * GIGABYTE, sessionBytes: 15 * GIGABYTE, highlightBytes: 2 * GIGABYTE, clipBytes: 1 * GIGABYTE },
    ],
    ...patch,
  };
}

function recording(patch: Partial<RecordingSettings> = {}): RecordingSettings {
  return {
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
    trashRetentionHours: 24,
    ...patch,
  };
}

function renderPage(overrides: {
  settings?: StorageSettings;
  recording?: RecordingSettings;
  status?: StorageStatusMessage | null;
  report?: StorageReportMessage | null;
} = {}) {
  const update = vi.fn(() => 'request-1');
  const onReclaim = vi.fn();
  const onRefresh = vi.fn();
  const onClearTrash = vi.fn();
  const onBrowse = vi.fn();
  render(
    <StoragePage
      settings={overrides.settings ?? settings()}
      recording={overrides.recording ?? recording()}
      update={update}
      page="storage"
      externalPushCount={0}
      status={overrides.status === undefined ? status() : overrides.status}
      report={overrides.report === undefined ? report() : overrides.report}
      onReclaim={onReclaim}
      onRefresh={onRefresh}
      onClearTrash={onClearTrash}
      onBrowse={onBrowse}
    />,
  );
  return { update, onReclaim, onRefresh, onClearTrash, onBrowse };
}

afterEach(() => {
  cleanup();
});

describe('StoragePage', () => {
  it('asks for a fresh count as soon as it opens', () => {
    const { onRefresh } = renderPage();
    expect(onRefresh).toHaveBeenCalled();
  });

  it('shows the split between sessions, highlights and clips', () => {
    renderPage();
    const legend = screen.getByTestId('storage-bar').textContent ?? '';

    expect(legend).toContain('Sessions');
    expect(legend).toContain('Highlights');
    expect(legend).toContain('Clips');
    expect(legend).toContain('Trash');
    expect(legend).toContain('Free');
  });

  it('lists each game with its own split', () => {
    renderPage();
    expect(screen.getByText('Overwatch')).toBeTruthy();
    expect(screen.getByText('Valorant')).toBeTruthy();
  });

  it('says there is room when the drive is healthy', () => {
    renderPage();
    expect(screen.getByText('There is room to record')).toBeTruthy();
  });

  it('names the recording drive, not a tighter drive the replays go to', () => {
    renderPage({ status: status({ scratchRoot: 'C:', scratchFreeBytes: 2 * GIGABYTE, scratchLow: true }) });

    expect(screen.getByText(/free on T:/)).toBeTruthy();
  });

  it('warns about the replay drive separately when it is the one that is short', () => {
    renderPage({ status: status({ scratchRoot: 'C:', scratchFreeBytes: 2 * GIGABYTE, scratchLow: true }) });
    const warning = screen.getByTestId('storage-scratch-warning');

    expect(warning.textContent).toContain('C:');
    expect(warning.textContent).toContain('2 GB');
  });

  it('says nothing about a replay drive that is fine', () => {
    renderPage();
    expect(screen.queryByTestId('storage-scratch-warning')).toBeNull();
  });

  it('says recording is on hold when it is blocked', () => {
    renderPage({ status: status({ pressure: 'critical', recordingBlocked: true, freeBytes: 2 * GIGABYTE }) });
    expect(screen.getByText(/Recording is on hold/)).toBeTruthy();
  });

  it('saves the reserved space in bytes and marks the policy as chosen', () => {
    const { update } = renderPage();
    const field = screen.getByLabelText('Keep this much free in GB');
    fireEvent.change(field, { target: { value: '50' } });
    fireEvent.blur(field);

    expect(update).toHaveBeenCalledWith('storage', {
      minimumFreeBytes: 50 * GIGABYTE,
      policyConfirmed: true,
    });
  });

  it('refuses a reserved space outside the allowed range', () => {
    const { update } = renderPage();
    const field = screen.getByLabelText('Keep this much free in GB') as HTMLInputElement;
    fireEvent.change(field, { target: { value: '0' } });
    fireEvent.blur(field);

    expect(update).not.toHaveBeenCalled();
    expect(field.value).toBe('20');
  });

  it('records the choice of policy', () => {
    const { update } = renderPage();
    fireEvent.click(screen.getByRole('radio', { name: 'Remove the oldest' }));

    expect(update).toHaveBeenCalledWith('storage', {
      whenFull: 'ReclaimOldest',
      policyConfirmed: true,
    });
  });

  it('keeps the sharing choice on its own', () => {
    const { update } = renderPage();
    fireEvent.click(screen.getByLabelText(/Keep sharing the game picture/));

    expect(update).toHaveBeenCalledWith('storage', { keepSharingWhenFull: false });
  });

  it('confirms before it frees anything', () => {
    const { onReclaim } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Free up space now' }));

    expect(onReclaim).not.toHaveBeenCalled();
    expect(screen.getByText(/Favourites are kept/)).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Free up space' }));
    expect(onReclaim).toHaveBeenCalled();
  });

  it('names what clearing the trash will destroy, and only does it after confirming', () => {
    const { onClearTrash } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Clear trash' }));

    expect(onClearTrash).not.toHaveBeenCalled();
    const notice = screen.getByText(/permanently deletes/);
    expect(notice.textContent).toContain('1 item(s)');
    expect(notice.textContent).toContain('1 GB');
    expect(notice.textContent).toContain('cannot be undone');

    fireEvent.click(screen.getAllByRole('button', { name: 'Clear trash' })[1]);
    expect(onClearTrash).toHaveBeenCalled();
  });

  it('will not offer to clear an already empty trash', () => {
    renderPage({ report: report({ trashCount: 0, trashBytes: 0 }) });
    expect((screen.getByRole('button', { name: 'Clear trash' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('keeps the recording folder here and writes it to the recording settings', () => {
    const { update } = renderPage();
    const field = screen.getByLabelText('Recording folder');
    fireEvent.change(field, { target: { value: '/home/tester/Videos/Tript' } });

    expect(update).toHaveBeenCalledWith('recording', { outputDirectory: '/home/tester/Videos/Tript' });
  });

  it('clears the recording folder back to the default', () => {
    const { update } = renderPage({ recording: recording({ outputDirectory: '/somewhere' }) });
    fireEvent.change(screen.getByLabelText('Recording folder'), { target: { value: '' } });

    expect(update).toHaveBeenCalledWith('recording', { outputDirectory: null });
  });

  it('opens the native picker from Browse', () => {
    const { onBrowse } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));

    expect(onBrowse).toHaveBeenCalled();
  });

  it('keeps the trash retention setting here', () => {
    const { update } = renderPage();
    fireEvent.change(screen.getByLabelText('Empty the trash after'), { target: { value: '168' } });

    expect(update).toHaveBeenCalledWith('recording', { trashRetentionHours: 168 });
  });

  it('shows an unusual stored retention as its own option', () => {
    renderPage({ recording: recording({ trashRetentionHours: 5 }) });
    expect(screen.getByText('5 hours (current)')).toBeTruthy();
  });

  it('reads a legacy zero or negative retention as Never', () => {
    renderPage({ recording: recording({ trashRetentionHours: -5 }) });
    const field = screen.getByLabelText('Empty the trash after') as HTMLSelectElement;

    expect(field.value).toBe('0');
    expect(field.selectedOptions[0].text).toBe('Never');
    expect(screen.queryByText('-5 hours (current)')).toBeNull();
  });

  it('shows only the biggest games until asked for the rest', () => {
    const many = Array.from({ length: 9 }, (_, index) => ({
      gameId: `g${index}`,
      name: `Game ${index}`,
      totalBytes: (9 - index) * GIGABYTE,
      sessionBytes: (9 - index) * GIGABYTE,
      highlightBytes: 0,
      clipBytes: 0,
    }));
    renderPage({ report: report({ games: many }) });

    expect(screen.queryByText('Game 7')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Show all 9 games' }));
    expect(screen.getByText('Game 7')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Show fewer' }));
    expect(screen.queryByText('Game 7')).toBeNull();
  });

  it('leaves the game list alone when it is short', () => {
    renderPage();
    expect(screen.queryByRole('button', { name: /Show all/ })).toBeNull();
  });

  it('waits for the count rather than drawing an empty bar', () => {
    renderPage({ report: null });
    expect(screen.queryByTestId('storage-bar')).toBeNull();
    expect(screen.getByText(/Working out what is using/)).toBeTruthy();
  });
});
