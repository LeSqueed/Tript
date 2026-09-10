// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { GamePage } from './GamePage';
import type { GameSearchResultsMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage, SettingsUpdateResultMessage } from '../../ipc/protocol';
import type { GameSettings, RecordingMode } from '../settingsModel';

const PACKAGED_ID = 'Overwatch';
const SETTINGS: GameSettings = {
  gameCaptureTimeout: 10,
  ignoredApplications: [],
  gameList: [
    {
      id: PACKAGED_ID,
      name: 'Overwatch',
      executablePath: 'C:\\Program Files\\Overwatch\\Overwatch.exe',
      captureMethodOverride: { method: 'Game' },
    },
    {
      id: 'custom-existing',
      name: 'Existing game',
      executablePath: 'D:\\Games\\Existing\\game.exe',
    },
  ],
};

function renderPage(
  settings: GameSettings = SETTINGS,
  selectedGameExecutable: SelectedGameExecutableMessage | null = null,
  globalClipBeforeSeconds = 5,
  globalClipAfterSeconds = 8,
  globalRecordingMode: RecordingMode = 'SessionWithReplayBuffer',
  automaticClipsEnabled = true,
) {
  const update = vi.fn((_page: string, _patch: Partial<Record<string, unknown>>) => 'settings-request-1');
  const onBrowseExecutable = vi.fn();
  const onSearchGames = vi.fn();
  const onResolveGameSearch = vi.fn();
  const view = (
    selected = selectedGameExecutable,
    settingsUpdateResult: SettingsUpdateResultMessage | null = null,
    gameSearchResults: GameSearchResultsMessage | null = null,
    resolvedGameSearch: ResolvedGameSearchMessage | null = null,
  ) => (
    <GamePage
      settings={settings}
      update={update}
      page="game"
      externalPushCount={0}
      builtInGameIds={[PACKAGED_ID]}
      selectedGameExecutable={selected}
      settingsUpdateResult={settingsUpdateResult}
      onBrowseExecutable={onBrowseExecutable}
      gameSearchResults={gameSearchResults}
      onSearchGames={onSearchGames}
      resolvedGameSearch={resolvedGameSearch}
      onResolveGameSearch={onResolveGameSearch}
      globalClipBeforeSeconds={globalClipBeforeSeconds}
      globalClipAfterSeconds={globalClipAfterSeconds}
      globalRecordingMode={globalRecordingMode}
      automaticClipsEnabled={automaticClipsEnabled}
    />
  );
  const result = render(view());
  return { ...result, update, onBrowseExecutable, onSearchGames, onResolveGameSearch, view };
}

function oneGame(override?: GameSettings['gameList'][number]['automaticClipOverride']) {
  return {
    gameCaptureTimeout: 10,
    ignoredApplications: [],
    gameList: [
      {
        id: PACKAGED_ID,
        name: 'Overwatch',

        ...(override !== undefined ? { automaticClipOverride: override } : {}),
      },
    ],
  };
}

function gameListFrom(update: ReturnType<typeof vi.fn>) {
  return (update.mock.calls.at(-1)?.[1] as { gameList: GameSettings['gameList'] }).gameList;
}

function selectResolvedGame(
  view: ReturnType<typeof renderPage>['view'],
  rerender: ReturnType<typeof renderPage>['rerender'],
  onSearchGames: ReturnType<typeof vi.fn>,
  name: string,
) {
  fireEvent.change(screen.getByLabelText('Find game'), { target: { value: name } });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
  const requestId = onSearchGames.mock.calls.at(-1)?.[0] as string;
  rerender(view(null, null, {
    requestId,
    results: [{ gameId: '01HRESOLVEDGAME000000000000', name, source: 'igdb' }],
  }));
  fireEvent.click(screen.getByRole('option', { name: new RegExp(name) }));
}

afterEach(cleanup);

describe('custom games', () => {
  it('adds a searched game with its canonical identity in one gameList update', () => {
    const { update, rerender, view, onSearchGames } = renderPage({ gameCaptureTimeout: 10, gameList: [], ignoredApplications: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    selectResolvedGame(view, rerender, onSearchGames, 'My Game');
    fireEvent.change(screen.getByLabelText('Executable path'), {
      target: { value: 'C:\\Games\\My Game\\game.exe' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(update).toHaveBeenCalledTimes(1);
    expect(update.mock.calls[0][0]).toBe('game');
    expect(gameListFrom(update)).toEqual([
      {
        id: '01HRESOLVEDGAME000000000000',
        name: 'My Game',
        executablePath: 'C:\\Games\\My Game\\game.exe',
      },
    ]);
    expect(screen.getByTestId('custom-game-draft')).toBeTruthy();
  });

  it('requires a name and an absolute executable path before saving', () => {
    const { update } = renderPage({ gameCaptureTimeout: 10, gameList: [], ignoredApplications: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.change(screen.getByLabelText('Executable path'), { target: { value: 'game.exe' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(screen.getByText('Enter a game name.')).toBeTruthy();
    expect(screen.getByText('Enter the exact executable path.')).toBeTruthy();
    expect(update).not.toHaveBeenCalled();
  });

  it('edits the custom name and path atomically without changing its id or overrides', () => {
    const { update } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Edit' }));
    fireEvent.change(screen.getByLabelText('Game name'), { target: { value: 'Renamed game' } });
    fireEvent.change(screen.getByLabelText('Executable path'), {
      target: { value: 'E:\\Renamed\\renamed.exe' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(update).toHaveBeenCalledTimes(1);
    expect(gameListFrom(update)[1]).toEqual({
      id: 'custom-existing',
      name: 'Renamed game',
      executablePath: 'E:\\Renamed\\renamed.exe',
    });
  });

  it('removes only the selected custom entry', () => {
    const { update } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    expect(gameListFrom(update)).toEqual([SETTINGS.gameList[0]]);
  });

  it('applies only the executable picker result correlated to the active request', () => {
    const { onBrowseExecutable, rerender, view } = renderPage({ gameCaptureTimeout: 10, gameList: [], ignoredApplications: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    const requestId = onBrowseExecutable.mock.calls[0][0] as string;
    expect(requestId).toEqual(expect.any(String));

    rerender(view({ requestId: 'another-request', filePath: 'C:\\Wrong\\wrong.exe' }));
    expect((screen.getByLabelText('Executable path') as HTMLInputElement).value).toBe('');

    rerender(view({ requestId, filePath: 'C:\\Picked\\picked.exe' }));
    expect((screen.getByLabelText('Executable path') as HTMLInputElement).value).toBe(
      'C:\\Picked\\picked.exe',
    );
  });

  it('treats picker cancellation as the correlated completion and ignores a later stale result', () => {
    const { onBrowseExecutable, rerender, view } = renderPage({ gameCaptureTimeout: 10, gameList: [], ignoredApplications: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.click(screen.getByRole('button', { name: 'Browse' }));
    const requestId = onBrowseExecutable.mock.calls[0][0] as string;

    rerender(view({ requestId, filePath: null }));
    rerender(view({ requestId, filePath: 'C:\\Late\\late.exe' }));

    expect((screen.getByLabelText('Executable path') as HTMLInputElement).value).toBe('');
    expect(screen.getByTestId('custom-game-draft')).toBeTruthy();
  });

  it('retains a valid draft and backend validation error when its update is rejected', () => {
    const { rerender, view, onSearchGames } = renderPage({ gameCaptureTimeout: 10, gameList: [], ignoredApplications: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    selectResolvedGame(view, rerender, onSearchGames, 'Rejected game');
    fireEvent.change(screen.getByLabelText('Executable path'), { target: { value: 'C:\\Games\\Rejected\\game.exe' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    rerender(view(null, { requestId: 'settings-request-1', success: false, error: 'That executable is already configured.' }));

    expect(screen.getByRole('option', { name: /Rejected game/ }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('alert').textContent).toBe('That executable is already configured.');
    expect(screen.getByLabelText('Executable path').getAttribute('aria-describedby')).toBe('custom-game-save-error');
  });

  it('closes a custom draft only after its correlated update succeeds', () => {
    const { rerender, view, onSearchGames } = renderPage({ gameCaptureTimeout: 10, gameList: [] });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    selectResolvedGame(view, rerender, onSearchGames, 'Accepted game');
    fireEvent.change(screen.getByLabelText('Executable path'), { target: { value: 'C:\\Games\\Accepted\\game.exe' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    rerender(view(null, { requestId: 'another-request', success: true }));
    expect(screen.getByTestId('custom-game-draft')).toBeTruthy();

    rerender(view(null, { requestId: 'settings-request-1', success: true }));
    expect(screen.queryByTestId('custom-game-draft')).toBeNull();
  });
});

describe('packaged games', () => {
  it('renders packaged identity and executable as immutable values', () => {
    renderPage();
    expect(screen.getByText('Overwatch', { selector: 'strong' })).toBeTruthy();
    expect(screen.getByText('C:\\Program Files\\Overwatch\\Overwatch.exe')).toBeTruthy();
    expect(screen.queryByDisplayValue('Overwatch')).toBeNull();
    expect(screen.getAllByRole('button', { name: 'Edit' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Remove' })).toHaveLength(1);
  });

  it('resets a packaged row to the minimal packaged identity', () => {
    const { update } = renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Reset overrides' }));
    expect(gameListFrom(update)).toEqual([
      { id: PACKAGED_ID, name: 'Overwatch' },
      SETTINGS.gameList[1],
    ]);
  });

  it('matches packaged ids case-insensitively', () => {
    renderPage({ ...SETTINGS, gameList: [{ ...SETTINGS.gameList[0], id: 'overwatch' }] });
    expect(screen.getByRole('button', { name: 'Reset overrides' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Edit' })).toBeNull();
  });
});

describe('automatic clip overrides', () => {
  it('renders the before and after fields as numeric globals-by-placeholder', () => {
    renderPage(oneGame());
    const before = screen.getByLabelText('Before (s)') as HTMLInputElement;
    const after = screen.getByLabelText('After (s)') as HTMLInputElement;
    expect(before.getAttribute('type')).toBe('number');
    expect(before.getAttribute('min')).toBe('1');
    expect(before.getAttribute('placeholder')).toBe('global');
    expect(before.value).toBe('');
    expect(after.value).toBe('');
  });

  it('typing a before-only override leaves the after side inheriting', () => {
    const { update } = renderPage(oneGame(), null, 5, 8);
    fireEvent.change(screen.getByLabelText('Before (s)'), { target: { value: '3' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(gameListFrom(update)[0]).toEqual({
      id: PACKAGED_ID,
      name: 'Overwatch',

      automaticClipOverride: { beforeSeconds: 3, afterSeconds: null },
    });
  });

  it('typing an after-only override leaves the before side inheriting', () => {
    const { update } = renderPage(oneGame(), null, 5, 8);
    fireEvent.change(screen.getByLabelText('After (s)'), { target: { value: '12' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: null,
      afterSeconds: 12,
    });
  });

  it('overriding one side keeps the other side’s stored override', () => {
    const { update } = renderPage(oneGame({ beforeSeconds: 2 }), null, 5, 8);
    fireEvent.change(screen.getByLabelText('After (s)'), { target: { value: '9' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: 2,
      afterSeconds: 9,
    });
  });

  it('clearing both sides back to inherit drops the override entirely', () => {
    const { update } = renderPage(oneGame({ beforeSeconds: 3 }), null, 5, 8);
    fireEvent.change(screen.getByLabelText('Before (s)'), { target: { value: '' } });

    expect(gameListFrom(update)[0]).toEqual({
      id: PACKAGED_ID,
      name: 'Overwatch',
    });
  });

  it('raising before above the effective after raises after in the same patch', () => {
    const { update } = renderPage(oneGame({ beforeSeconds: 3 }), null, 5, 8);
    fireEvent.change(screen.getByLabelText('Before (s)'), { target: { value: '10' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: 10,
      afterSeconds: 10,
    });
  });

  it('raises inherited after when before outruns the global window', () => {
    const { update } = renderPage(oneGame(), null, 5, 8);
    fireEvent.change(screen.getByLabelText('Before (s)'), { target: { value: '10' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: 10,
      afterSeconds: 10,
    });
  });

  it('clamps a typed after below the effective before up to it', () => {
    const { update } = renderPage(oneGame({ beforeSeconds: 4 }), null, 5, 8);
    fireEvent.change(screen.getByLabelText('After (s)'), { target: { value: '2' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: 4,
      afterSeconds: 4,
    });
  });

  it('clamps after against the inherited global before when nothing is overridden', () => {
    const { update } = renderPage(oneGame(), null, 5, 8);
    fireEvent.change(screen.getByLabelText('After (s)'), { target: { value: '3' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: null,
      afterSeconds: 5,
    });
  });

  it('clearing after below the effective before clamps it up rather than sending an invalid pair', () => {
    const { update } = renderPage(oneGame({ beforeSeconds: 10, afterSeconds: 12 }), null, 5, 8);
    fireEvent.change(screen.getByLabelText('After (s)'), { target: { value: '' } });

    expect(gameListFrom(update)[0].automaticClipOverride).toEqual({
      beforeSeconds: 10,
      afterSeconds: 10,
    });
  });

  it('resolves a selected external search result before saving its canonical identity', () => {
    const { update, rerender, view, onSearchGames, onResolveGameSearch } = renderPage({
      gameCaptureTimeout: 10,
      gameList: [],
      ignoredApplications: [],
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add custom game' }));
    fireEvent.change(screen.getByLabelText('Find game'), { target: { value: 'External Game' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    const searchRequestId = onSearchGames.mock.calls[0][0] as string;
    rerender(view(null, null, {
      requestId: searchRequestId,
      results: [{ name: 'External Game', source: 'igdb', igdbId: 456 }],
    }));
    fireEvent.click(screen.getByRole('option', { name: /External Game/ }));
    expect(onResolveGameSearch).toHaveBeenCalledWith(expect.any(String), 'igdb:456');
    const resolveRequestId = onResolveGameSearch.mock.calls[0][0] as string;
    rerender(view(null, null, null, {
      requestId: resolveRequestId,
      game: { gameId: '01HEXTERNALGAME0000000000000', name: 'External Game' },
    }));
    fireEvent.change(screen.getByLabelText('Executable path'), {
      target: { value: 'C:\\Games\\External\\game.exe' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(gameListFrom(update)[0].id).toBe('01HEXTERNALGAME0000000000000');
  });

  it('disables overrides when automatic highlights are globally off', () => {
    renderPage(oneGame(), null, 5, 8, 'SessionWithReplayBuffer', false);

    expect((screen.getByLabelText('Before (s)') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('After (s)') as HTMLInputElement).disabled).toBe(true);
  });

  it('disables overrides when the game resolves to Session mode', () => {
    renderPage({
      ...oneGame(),
      gameList: [{
        ...oneGame().gameList[0],
        recordingModeOverride: { mode: 'Session' },
      }],
    });

    expect((screen.getByLabelText('Before (s)') as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText('After (s)') as HTMLInputElement).disabled).toBe(true);
  });

  it('enables overrides when the game resolves to Replay buffer only mode', () => {
    renderPage({
      ...oneGame(),
      gameList: [{
        ...oneGame().gameList[0],
        recordingModeOverride: { mode: 'ReplayBufferOnly' },
      }],
    });

    expect((screen.getByLabelText('Before (s)') as HTMLInputElement).disabled).toBe(false);
    expect((screen.getByLabelText('After (s)') as HTMLInputElement).disabled).toBe(false);
  });
});

describe('ignored applications', () => {
  const ignoredSettings: GameSettings = {
    gameCaptureTimeout: 10,
    gameList: [],
    ignoredApplications: [
      'C:\\Tools\\overlay.exe',
      'D:\\Utilities\\metrics.exe',
    ],
  };

  it('keeps the list collapsed and reports its size', () => {
    renderPage(ignoredSettings);

    const disclosure = screen.getByText('Ignored applications').closest('details');
    expect(disclosure?.open).toBe(false);
    expect(disclosure?.textContent).toContain('2');
  });

  it('filters a large ignored list and removes the original entry', () => {
    const { update } = renderPage(ignoredSettings);
    fireEvent.click(screen.getByText('Ignored applications'));
    fireEvent.change(screen.getByLabelText('Filter ignored applications'), { target: { value: 'metrics' } });

    expect(screen.queryByText('overlay.exe')).toBeNull();
    expect(screen.getByText('metrics.exe')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Remove metrics.exe from ignored applications' }));

    expect(update).toHaveBeenLastCalledWith('game', {
      ignoredApplications: ['C:\\Tools\\overlay.exe'],
    });
  });
});

describe('per-game override disclosure', () => {
  it('shows the disclosure without a modified marker when the game has no overrides', () => {
    const { container } = renderPage(oneGame());
    expect(screen.getByText('Customize for this game')).toBeTruthy();
    const details = container.querySelector('details.settings-advanced');
    expect(details).toBeTruthy();
    expect(details?.querySelector('summary')?.textContent).toBe('Customize for this game');
    expect(screen.queryByText('modified')).toBeNull();
    expect(screen.getByLabelText('Before (s)')).toBeTruthy();
    expect(screen.getByLabelText('After (s)')).toBeTruthy();
  });

  it('shows the modified marker when the game has an override set', () => {
    renderPage({
      gameCaptureTimeout: 10,
      gameList: [
        {
          id: PACKAGED_ID,
          name: 'Overwatch',

          qualityOverride: { fps: 144 },
        },
      ],
    });
    const marker = screen.getByText('modified');
    expect(marker.className).toBe('settings-advanced-marker');
  });

  it('does not mark empty legacy override objects as modified', () => {
    renderPage({
      ...oneGame(),
      gameList: [{
        ...oneGame().gameList[0],
        recordingModeOverride: {} as never,
        captureMethodOverride: {} as never,
      }],
    });

    expect(screen.queryByText('modified')).toBeNull();
  });

  it('sends the same gameList patch as before when an override is set through the select', () => {
    const { update } = renderPage(oneGame());
    fireEvent.change(screen.getByLabelText('Recording mode'), { target: { value: 'SessionWithReplayBuffer' } });

    expect(update).toHaveBeenCalledTimes(1);
    expect(gameListFrom(update)[0]).toEqual({
      id: PACKAGED_ID,
      name: 'Overwatch',

      recordingModeOverride: { mode: 'SessionWithReplayBuffer' },
    });
  });

  it('offers and sends the Replay buffer only per-game override', () => {
    const { update } = renderPage(oneGame());
    fireEvent.change(screen.getByLabelText('Recording mode'), { target: { value: 'ReplayBufferOnly' } });

    expect(gameListFrom(update)[0].recordingModeOverride).toEqual({ mode: 'ReplayBufferOnly' });
  });
});
