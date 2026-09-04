// SPDX-License-Identifier: GPL-2.0-or-later
//
// Tests for the capture page: the capture-method copy, the display picker's visibility per method,
// and the game-capture timeout that now lives in this page's advanced disclosure but patches the
// game page.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { CapturePage } from './CapturePage';
import type { CaptureSettings, DisplayInfo, GameSettings } from '../settingsModel';

const DISPLAYS: DisplayInfo[] = [
  { id: 'DP-1', name: 'DP-1', width: 1920, height: 1080, primary: true },
  { id: 'HDMI-1', name: 'HDMI-1', width: 2560, height: 1440, primary: false },
];

const GAME: GameSettings = { gameCaptureTimeout: 8, gameList: [] };

function renderPage(
  settings: CaptureSettings = { method: 'Auto', display: null },
  availableDisplays: DisplayInfo[] | null = DISPLAYS,
) {
  const update = vi.fn().mockReturnValue('req-1');
  render(
    <CapturePage
      settings={settings}
      game={GAME}
      update={update}
      page="capture"
      availableDisplays={availableDisplays}
      externalPushCount={0}
    />,
  );
  return update;
}

afterEach(cleanup);

describe('capture method', () => {
  it('renders the three plain labels', () => {
    renderPage();

    const select = screen.getByLabelText(/^Capture method/) as HTMLSelectElement;
    expect(Array.from(select.options).map((option) => option.textContent)).toEqual([
      'Automatic',
      'Game window',
      'A specific monitor',
    ]);
  });

  it('sends just the method when switching to the game window', () => {
    const update = renderPage();

    fireEvent.change(screen.getByLabelText(/^Capture method/), { target: { value: 'Game' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('capture', { method: 'Game' });
  });
});

describe('display picker visibility', () => {
  it('renders for Automatic', () => {
    renderPage({ method: 'Auto', display: null });

    expect(screen.getByTestId('capture-display-select')).toBeTruthy();
  });

  it('renders for a specific monitor', () => {
    renderPage({ method: 'Display', display: 'DP-1' });

    expect(screen.getByTestId('capture-display-select')).toBeTruthy();
  });

  it('hides for the game window', () => {
    renderPage({ method: 'Game', display: null });

    expect(screen.queryByTestId('capture-display-select')).toBeNull();
  });
});

describe('game-capture timeout', () => {
  it('sits in the advanced disclosure and commits to the game page on blur', () => {
    const update = renderPage();

    expect(screen.getByText('Advanced')).toBeTruthy();
    const input = screen.getByLabelText('Game-capture timeout') as HTMLInputElement;
    expect(input).toBeTruthy();

    fireEvent.change(input, { target: { value: '12' } });
    fireEvent.blur(input);

    expect(update).toHaveBeenCalledTimes(1);
    expect(update).toHaveBeenCalledWith('game', { gameCaptureTimeout: 12 });
  });

  it('commits on Enter as well', () => {
    const update = renderPage();

    const input = screen.getByLabelText('Game-capture timeout') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '5' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(update).toHaveBeenCalledWith('game', { gameCaptureTimeout: 5 });
  });

  it('reverts an invalid value rather than sending it', () => {
    const update = renderPage();

    const input = screen.getByLabelText('Game-capture timeout') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '0' } });
    fireEvent.blur(input);

    expect(update).not.toHaveBeenCalled();
    expect(input.value).toBe('8');
  });

  it('preserves newer typing across a self echo and resyncs on an external push', () => {
    const update = vi.fn().mockReturnValue('req-1');
    const capture: CaptureSettings = { method: 'Auto', display: null };
    const view = (game: GameSettings, externalPushCount: number) => (
      <CapturePage settings={capture} game={game} update={update} page="capture"
        availableDisplays={DISPLAYS} externalPushCount={externalPushCount} />
    );
    const { rerender } = render(view(GAME, 0));
    const input = screen.getByLabelText('Game-capture timeout') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '14' } });

    rerender(view({ ...GAME, gameCaptureTimeout: 12 }, 0));
    expect(input.value).toBe('14');

    rerender(view({ ...GAME, gameCaptureTimeout: 20 }, 1));
    expect(input.value).toBe('20');
  });
});
