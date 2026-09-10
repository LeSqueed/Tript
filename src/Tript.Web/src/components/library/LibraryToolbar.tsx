// SPDX-License-Identifier: GPL-2.0-or-later

import { Button, Field, SegmentedControl, SelectField, TextField, Toggle, type Segment, type SelectOption } from '../ui/controls';
import type { ContentTypeFilter, DateRangeFilter, LibraryQuery, LibrarySort } from './libraryModel';

interface LibraryToolbarProps {
  query: LibraryQuery;
  typeSegments: Segment<ContentTypeFilter>[];
  gameOptions: SelectOption[];
  dateOptions: SelectOption[];
  sortOptions: SelectOption[];
  showingTrash: boolean;
  filtered: boolean;
  onQueryChange: (patch: Partial<LibraryQuery>) => void;
  onClearFilters: () => void;
}

export function LibraryToolbar({
  query,
  typeSegments,
  gameOptions,
  dateOptions,
  sortOptions,
  showingTrash,
  filtered,
  onQueryChange,
  onClearFilters,
}: LibraryToolbarProps) {
  return (
    <div className="library-toolbar">
      {}
      <SegmentedControl
        label="Show"
        value={query.type}
        segments={typeSegments}
        onChange={(value) => onQueryChange({ type: value })}
      />

      <div className="library-filters">
        <LibrarySelectFilters
          query={query}
          gameOptions={gameOptions}
          dateOptions={dateOptions}
          sortOptions={sortOptions}
          onQueryChange={onQueryChange}
        />
        {!showingTrash && (
          <Toggle
            checked={query.favoriteOnly}
            onChange={(checked) => onQueryChange({ favoriteOnly: checked })}
            label="Favourites only"
          />
        )}
        {filtered && (
          <Button variant="ghost" className="library-clear" onClick={onClearFilters}>
            Clear filters
          </Button>
        )}
      </div>
    </div>
  );
}

export function LibrarySelectFilters({
  query,
  gameOptions,
  dateOptions,
  sortOptions,
  onQueryChange,
}: Pick<LibraryToolbarProps, 'query' | 'gameOptions' | 'dateOptions' | 'sortOptions' | 'onQueryChange'>) {
  return (
    <>
      <Field label="Game">
        <SelectField value={query.game} onChange={(value) => onQueryChange({ game: value })} options={gameOptions} />
      </Field>
      <Field label="Date">
        <SelectField
          value={query.range}
          onChange={(value) => onQueryChange({ range: value as DateRangeFilter })}
          options={dateOptions}
        />
      </Field>
      <Field label="Sort">
        <SelectField
          value={query.sort}
          onChange={(value) => onQueryChange({ sort: value as LibrarySort })}
          options={sortOptions}
        />
      </Field>
      <Field label="Search">
        <TextField value={query.search} onChange={(value) => onQueryChange({ search: value })} placeholder="Title or game" />
      </Field>
    </>
  );
}
