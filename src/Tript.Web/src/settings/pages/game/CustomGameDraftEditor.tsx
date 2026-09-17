// SPDX-License-Identifier: GPL-2.0-or-later

import type { GameSetting } from '../../settingsModel';
import { Button, TextField } from '../../../components/ui/controls';
import { isAbsoluteExecutablePath, type CustomGameDraft, type CustomGameDraftState } from './useCustomGameDraft';

export function CustomGameDraftEditor({
  state,
  draft,
  gameList,
  builtInIds,
}: {
  state: CustomGameDraftState;
  draft: CustomGameDraft;
  gameList: GameSetting[];
  builtInIds: ReadonlySet<string>;
}) {
  const { validationAttempted, submissionError } = state;
  const nameMissing = validationAttempted && draft.name.trim() === '';
  const executableInvalid = validationAttempted && !isAbsoluteExecutablePath(draft.executablePath.trim());

  return (
    <div className="game-row game-draft" data-testid="custom-game-draft">
      <h4 className="game-row-title">{draft.index === null ? 'Add custom game' : 'Edit custom game'}</h4>
      <div className="game-draft-fields">
        {draft.index === null ? <div className="field game-search-field">
          <label className="field-label" htmlFor="custom-game-search">Find game</label>
          <div className="game-executable-field">
            <TextField
              id="custom-game-search"
              value={state.searchQuery}
              onChange={state.setSearchQuery}
              placeholder="Search by game title"
              onKeyDown={(event) => { if (event.key === 'Enter') state.search(); }}
            />
            <Button variant="ghost" onClick={state.search} disabled={state.searching}>
              {state.searching ? 'Searching…' : 'Search'}
            </Button>
          </div>
          {state.searchError && <p className="game-validation" role="alert">{state.searchError}</p>}
          {state.searchResults.length > 0 && <div className="game-search-results" role="listbox" aria-label="Game search results">
            {state.searchResults.map((result, index) => {
              const id = result.gameId?.trim();
              const duplicate = id ? gameList.some((game) => game.id.toLowerCase() === id.toLowerCase()) || builtInIds.has(id.toLowerCase()) : false;
              const resolvable = Boolean(id || result.igdbId || result.steamAppId);
              const disabled = !resolvable || duplicate || state.resolving;
              return <button
                type="button"
                role="option"
                aria-selected={draft.selectedGame === result}
                className={draft.selectedGame === result ? 'game-search-result selected' : 'game-search-result'}
                disabled={disabled}
                key={`${id ?? result.source}-${result.name}-${index}`}
                onClick={() => state.selectResult(result)}
              >
                <strong>{result.name}{result.year ? ` (${result.year})` : ''}</strong>
                <span>{[result.platforms, result.source, duplicate ? 'Already configured' : !resolvable ? 'Cannot resolve' : null].filter(Boolean).join(' · ')}</span>
              </button>;
            })}
          </div>}
        </div> : <div className="field">
          <label className="field-label" htmlFor="custom-game-name">Game name</label>
          <TextField
            id="custom-game-name"
            value={draft.name}
            onChange={state.setName}
            aria-invalid={nameMissing}
            aria-describedby={nameMissing ? 'custom-game-name-error' : submissionError ? 'custom-game-save-error' : undefined}
          />
        </div>}
        <div className="field">
          <label className="field-label" htmlFor="custom-game-executable">Executable path</label>
          <div className="game-executable-field">
            <TextField
              id="custom-game-executable"
              value={draft.executablePath}
              onChange={state.setExecutablePath}
              placeholder="C:\\Games\\Example\\game.exe"
              aria-invalid={executableInvalid}
              aria-describedby={executableInvalid ? 'custom-game-executable-error' : submissionError ? 'custom-game-save-error' : undefined}
            />
            <Button variant="ghost" onClick={state.browse}>Browse</Button>
          </div>
        </div>
      </div>
      {nameMissing && <p id="custom-game-name-error" className="game-validation" role="alert">Enter a game name.</p>}
      {validationAttempted && draft.index === null && !draft.selectedGame?.gameId && <p className="game-validation" role="alert">Select a game from the search results.</p>}
      {executableInvalid && <p id="custom-game-executable-error" className="game-validation" role="alert">Enter the exact executable path.</p>}
      {submissionError && <p id="custom-game-save-error" className="game-validation" role="alert">{submissionError}</p>}
      <div className="game-draft-actions">
        <Button onClick={state.save} disabled={state.saving}>Save</Button>
        <Button variant="ghost" onClick={state.cancel}>Cancel</Button>
      </div>
    </div>
  );
}
