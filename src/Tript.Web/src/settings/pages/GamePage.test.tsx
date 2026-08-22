// SPDX-License-Identifier: GPL-2.0-or-later
//
// The catalogue owns game identities and executables; this page only edits per-game overrides.

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

describe('the game page catalogue boundary', () => {
  it('explains that the catalogue owns game identities and executables', () => {
    renderPage();
    expect(screen.getByText(/Stable game identities and known executables come from/i)).toBeTruthy();
    expect(screen.getByText(/Games and executables are maintained in the project catalogue/i)).toBeTruthy();
  });

  it('does not offer a control for adding a non-catalogue game', () => {
    renderPage();
    expect(screen.queryByRole('button', { name: 'Add game' })).toBeNull();
  });

  it('still sends a per-game display-name override', () => {
    const { update } = renderPage();
    fireEvent.change(screen.getByDisplayValue('Counter-Strike 2'), {
      target: { value: 'CS2' },
    });

    expect(update).toHaveBeenCalledTimes(1);
    const [page, patch] = update.mock.calls[0];
    expect(page).toBe('game');
    expect((patch as { gameList: { id: string; name: string }[] }).gameList).toEqual([
      { id: 'cs2', name: 'CS2', integrations: { enabled: false } },
      { id: 'ow', name: 'Overwatch', executable: 'Overwatch.exe', integrations: { enabled: false } },
    ]);
  });
});
