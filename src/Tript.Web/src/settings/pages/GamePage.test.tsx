// SPDX-License-Identifier: GPL-2.0-or-later
//
// The executable field on the game page. The display name is not the process name often enough that
// the page has to say so, and has to show what a blank field falls back to.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { GamePage } from './GamePage';
import type { GameSettings } from '../settingsModel';

const SETTINGS: GameSettings = {
  captureMode: 'Auto',
  gameCaptureTimeout: 10,
  gameList: [
    { id: 'cs2', name: 'Counter-Strike 2', integrations: { enabled: false } },
    { id: 'ow', name: 'Overwatch', executable: 'Overwatch.exe', integrations: { enabled: false } },
  ],
};

function renderPage(settings: GameSettings = SETTINGS) {
  const update = vi.fn();
  render(
    <GamePage settings={settings} update={update} page="game" externalPushCount={0} />,
  );
  return { update };
}

afterEach(cleanup);

describe('the game page executable field', () => {
  it('explains what the executable is, and that blank means the name', () => {
    renderPage();
    const explanation = screen.getByText(/process name the recorder watches for/i);
    expect(explanation.textContent).toMatch(/cs2/);
    expect(explanation.textContent).toMatch(/blank/i);
  });

  // The placeholder is the game's own name, so the fallback is not something the user has to recall.
  it('offers the game name as the placeholder when nothing is set', () => {
    renderPage();
    const field = screen.getByLabelText('Executable for Counter-Strike 2') as HTMLInputElement;
    expect(field.value).toBe('');
    expect(field.placeholder).toBe('Counter-Strike 2');
  });

  it('shows the stored executable when there is one', () => {
    renderPage();
    expect((screen.getByLabelText('Executable for Overwatch') as HTMLInputElement).value)
      .toBe('Overwatch.exe');
  });

  it('sends the edited executable on the game it belongs to, leaving the others alone', () => {
    const { update } = renderPage();
    fireEvent.change(screen.getByLabelText('Executable for Counter-Strike 2'), {
      target: { value: 'cs2' },
    });

    expect(update).toHaveBeenCalledTimes(1);
    const [page, patch] = update.mock.calls[0];
    expect(page).toBe('game');
    expect((patch as { gameList: { id: string; executable?: string | null }[] }).gameList).toEqual([
      { id: 'cs2', name: 'Counter-Strike 2', executable: 'cs2', integrations: { enabled: false } },
      { id: 'ow', name: 'Overwatch', executable: 'Overwatch.exe', integrations: { enabled: false } },
    ]);
  });

  it('sends null when the field is cleared, not an empty process name', () => {
    const { update } = renderPage();
    fireEvent.change(screen.getByLabelText('Executable for Overwatch'), { target: { value: '' } });

    const [, patch] = update.mock.calls[0];
    const list = (patch as { gameList: { executable?: string | null }[] }).gameList;
    expect(list[1].executable).toBeNull();
  });
});
