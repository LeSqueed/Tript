// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentItem } from '../../ipc/protocol';
import {
  contentTypeLabel,
  contentLabel,
  formatContentDuration,
  formatContentSize,
} from '../contentPresentation';
import {
  ANY_GAME,
  NO_GAME,
  DATE_RANGE_SECONDS,
  cleanGame,
  typeMatches,
  gameMatches,
  dateMatches,
  searchMatches,
  filterCatalogue,
  sortCatalogue,
  type CatalogueRecord,
  type ContentTypeFilter,
  type DateRangeFilter,
  type LibrarySort,
} from '../catalogueModel';

export {
  ANY_GAME,
  NO_GAME,
  DATE_RANGE_SECONDS,
  type ContentTypeFilter,
  type DateRangeFilter,
  type LibrarySort,
};

export const UNKNOWN_GAME_LABEL = 'Unknown game';

export const UNKNOWN_DATE_LABEL = 'No date';

export const DEFAULT_PAGE_SIZE = 12;

export interface LibraryQuery {
  type: ContentTypeFilter;
  game: string;
  range: DateRangeFilter;
  search: string;
  sort: LibrarySort;
  page: number;
  pageSize: number;
  favoriteOnly: boolean;
}

export const DEFAULT_LIBRARY_QUERY: LibraryQuery = {
  type: 'all',
  game: ANY_GAME,
  range: 'any',
  search: '',
  sort: 'newest',
  page: 1,
  pageSize: DEFAULT_PAGE_SIZE,
  favoriteOnly: false,
};

export function itemGame(item: ContentItem): string | null {
  return cleanGame(item.game);
}

export function itemDate(item: ContentItem): number | undefined {
  const start = item.startTime;
  return typeof start === 'number' && Number.isFinite(start) && start > 0 ? start : undefined;
}

export function itemDuration(item: ContentItem): number | undefined {
  for (const candidate of [item.durationSeconds, item.endTime]) {
    if (typeof candidate === 'number' && Number.isFinite(candidate) && candidate > 0) {
      return candidate;
    }
  }
  return undefined;
}

export function itemLabel(item: ContentItem): string {
  return contentLabel(item.title, item.fileName);
}

export function toContentRecord(item: ContentItem): CatalogueRecord {
  return {
    contentType: item.contentType,
    game: itemGame(item),
    title: item.title ?? '',
    fileName: item.fileName,
    date: itemDate(item),
    label: itemLabel(item),
  };
}

export function typeLabel(item: ContentItem): string {
  return contentTypeLabel(item.contentType);
}

export interface RecordingGroup {
  recording: ContentItem | null;
  clips: ContentItem[];
}

export function isClipContent(item: ContentItem): boolean {
  return item.contentType === 'clip' || item.contentType === 'highlight';
}

export function linkedAutomaticHighlights(
  recording: ContentItem,
  candidates: readonly ContentItem[],
): ContentItem[] {
  return candidates
    .filter(
      (item) => item.automated === true && item.sourceSessionPath === recording.filePath,
    )
    .sort(
      (left, right) =>
        (left.clipStartTime ?? Number.POSITIVE_INFINITY) -
        (right.clipStartTime ?? Number.POSITIVE_INFINITY),
    );
}

export function cascadableLinkedHighlights(
  recording: ContentItem,
  candidates: readonly ContentItem[],
): ContentItem[] {
  return linkedAutomaticHighlights(recording, candidates).filter(
    (item) => item.favorite !== true,
  );
}

function baseName(fileName: string): string {
  const dot = fileName.lastIndexOf('.');
  return dot <= 0 ? fileName : fileName.slice(0, dot);
}

function sourceOf(clip: ContentItem, recordings: readonly ContentItem[]): ContentItem | null {
  if (clip.sourceSessionPath) {
    return recordings.find((recording) => recording.filePath === clip.sourceSessionPath) ?? null;
  }

  const clipBase = baseName(clip.fileName);
  let source: ContentItem | null = null;
  let matched = 0;

  for (const recording of recordings) {
    const recordingBase = baseName(recording.fileName);
    if (recordingBase.length <= matched) continue;
    if (!clipBase.startsWith(recordingBase)) continue;
    if (clipBase.length !== recordingBase.length && clipBase[recordingBase.length] !== '-') continue;
    source = recording;
    matched = recordingBase.length;
  }

  return source;
}

export function sourceSession(item: ContentItem, candidates: readonly ContentItem[]): ContentItem | null {
  if (item.contentType === 'recording') {
    return item;
  }
  if (!isClipContent(item)) {
    return null;
  }
  return sourceOf(item, candidates.filter((candidate) => candidate.contentType === 'recording'));
}

export function sessionPlaylist(recording: ContentItem, candidates: readonly ContentItem[]): ContentItem[] {
  const children = candidates
    .filter((item) => isClipContent(item) && sourceOf(item, [recording]) !== null)
    .sort((left, right) => {
      const byTimeline = (left.clipStartTime ?? Number.POSITIVE_INFINITY)
        - (right.clipStartTime ?? Number.POSITIVE_INFINITY);
      return byTimeline !== 0 ? byTimeline : itemLabel(left).localeCompare(itemLabel(right));
    });
  const playableMain = recording.videoMissing !== true
    && recording.highlightsOnly !== true
    && recording.recording !== true;
  return playableMain ? [recording, ...children] : children;
}

export function lacksMainVideo(item: ContentItem): boolean {
  return !isClipContent(item) && (item.videoMissing === true || item.highlightsOnly === true);
}

export function recordingChildCounts(items: readonly ContentItem[]): Map<string, { clips: number; highlights: number }> {
  const counts = new Map<string, { clips: number; highlights: number }>();
  for (const group of groupByRecording(items)) {
    if (!group.recording) continue;
    counts.set(group.recording.filePath, {
      clips: group.clips.length,
      highlights: group.clips.filter((clip) => clip.automated).length,
    });
  }
  return counts;
}

export function sessionPreviewHighlights(
  item: ContentItem,
  items: readonly ContentItem[],
): ContentItem[] | undefined {
  return item.videoMissing === true || item.highlightsOnly === true || item.recording === true
    ? linkedAutomaticHighlights(item, items)
    : undefined;
}

export function groupByRecording(items: readonly ContentItem[]): RecordingGroup[] {
  const recordings = items.filter((item) => !isClipContent(item));
  const groups = new Map<ContentItem, RecordingGroup>();
  const ordered: RecordingGroup[] = [];

  for (const item of items) {
    if (!isClipContent(item)) {
      const group: RecordingGroup = { recording: item, clips: [] };
      groups.set(item, group);
      ordered.push(group);
    } else if (sourceOf(item, recordings) === null) {
      ordered.push({ recording: null, clips: [item] });
    }
  }

  for (const item of items) {
    if (!isClipContent(item)) continue;
    const source = sourceOf(item, recordings);
    if (source !== null) {
      groups.get(source)!.clips.push(item);
    }
  }

  return ordered;
}

export function formatDurationChip(item: ContentItem): string | null {
  const seconds = itemDuration(item);
  return formatContentDuration(seconds);
}

export function formatDateChip(item: ContentItem): string {
  const seconds = itemDate(item);
  if (seconds === undefined) {
    return UNKNOWN_DATE_LABEL;
  }
  return new Date(seconds * 1000).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}

export function formatSizeChip(item: ContentItem): string | null {
  return formatContentSize(item.fileSizeBytes);
}

export function formatBytes(bytes: number | undefined): string | null {
  return formatContentSize(bytes);
}

export function matchesType(item: ContentItem, filter: ContentTypeFilter): boolean {
  return typeMatches(toContentRecord(item), filter);
}

export function matchesGame(item: ContentItem, game: string): boolean {
  return gameMatches(toContentRecord(item), game);
}

export function matchesDate(item: ContentItem, range: DateRangeFilter, nowSeconds: number): boolean {
  return dateMatches(toContentRecord(item), range, nowSeconds);
}

export function matchesSearch(item: ContentItem, search: string): boolean {
  return searchMatches(toContentRecord(item), search);
}

export function filterItems(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): ContentItem[] {
  return filterCatalogue(
    items,
    query,
    nowSeconds,
    toContentRecord,
    (item) =>
      (query.type !== 'sessions' || !lacksMainVideo(item)) &&
      (!query.favoriteOnly || item.favorite === true),
  );
}

export function isFiltered(query: LibraryQuery): boolean {
  return (
    query.type !== 'all' ||
    query.game !== ANY_GAME ||
    query.range !== 'any' ||
    query.search.trim().length > 0
    || query.favoriteOnly
  );
}

export function availableGames(items: readonly ContentItem[]): {
  names: string[];
  hasUnknown: boolean;
} {
  const seen = new Map<string, string>();
  let hasUnknown = false;
  for (const item of items) {
    const game = itemGame(item);
    if (game === null) {
      hasUnknown = true;
      continue;
    }
    const key = game.toLowerCase();
    if (!seen.has(key)) {
      seen.set(key, game);
    }
  }
  const names = [...seen.values()].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }));
  return { names, hasUnknown };
}

export function sortItems(items: readonly ContentItem[], sort: LibrarySort): ContentItem[] {
  return sortCatalogue(items, sort, toContentRecord);
}

export function pageCountFor(total: number, pageSize: number): number {
  const size = normalizePageSize(pageSize);
  return Math.max(1, Math.ceil(Math.max(0, total) / size));
}

export function clampPage(page: number, pageCount: number): number {
  const pages = Math.max(1, Math.floor(pageCount) || 1);
  if (!Number.isFinite(page)) {
    return 1;
  }
  return Math.min(Math.max(1, Math.floor(page)), pages);
}

function normalizePageSize(pageSize: number): number {
  return Number.isFinite(pageSize) && pageSize >= 1 ? Math.floor(pageSize) : DEFAULT_PAGE_SIZE;
}

export interface LibraryGroupPage {
  groups: RecordingGroup[];
  resultItems: ContentItem[];
  matchCount: number;
  totalCount: number;
  groupCount: number;
  page: number;
  pageCount: number;
  filtered: boolean;
}

export function deriveGroupedLibrary(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryGroupPage {
  const matched = sortItems(filterItems(items, query, nowSeconds), query.sort);
  const groups = groupByRecording(matched);
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(groups.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  return {
    groups: groups.slice(start, start + pageSize),
    resultItems: matched,
    matchCount: matched.length,
    totalCount: items.length,
    groupCount: groups.length,
    page,
    pageCount,
    filtered: isFiltered(query),
  };
}

export interface LibraryPage {
  items: ContentItem[];
  resultItems: ContentItem[];
  matchCount: number;
  totalCount: number;
  page: number;
  pageCount: number;
  firstIndex: number;
  lastIndex: number;
  filtered: boolean;
}

export function deriveLibrary(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryPage {
  const matched = sortItems(filterItems(items, query, nowSeconds), query.sort);
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(matched.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  const pageItems = matched.slice(start, start + pageSize);
  return {
    items: pageItems,
    resultItems: matched,
    matchCount: matched.length,
    totalCount: items.length,
    page,
    pageCount,
    firstIndex: pageItems.length === 0 ? 0 : start + 1,
    lastIndex: pageItems.length === 0 ? 0 : start + pageItems.length,
    filtered: isFiltered(query),
  };
}

export const DATE_OPTIONS: { value: string; label: string }[] = [
  { value: 'any', label: 'Any time' },
  { value: 'day', label: 'Last 24 hours' },
  { value: 'week', label: 'Last 7 days' },
  { value: 'month', label: 'Last 30 days' },
  { value: 'year', label: 'Last year' },
];

export const SORT_OPTIONS: { value: string; label: string }[] = [
  { value: 'newest', label: 'Newest first' },
  { value: 'oldest', label: 'Oldest first' },
  { value: 'game', label: 'Game (A–Z)' },
];

export function deriveSessions(
  items: readonly ContentItem[],
  query: LibraryQuery,
  nowSeconds: number,
): LibraryPage {
  const sessions = items.filter((item) => !isClipContent(item));
  const matched = sortItems(
    sessions.filter(
      (item) =>
        matchesGame(item, query.game) &&
        matchesDate(item, query.range, nowSeconds) &&
        (!query.favoriteOnly || item.favorite === true) &&
        matchesSearch(item, query.search),
    ),
    query.sort,
  );
  const pageSize = normalizePageSize(query.pageSize);
  const pageCount = pageCountFor(matched.length, pageSize);
  const page = clampPage(query.page, pageCount);
  const start = (page - 1) * pageSize;
  const pageItems = matched.slice(start, start + pageSize);
  return {
    items: pageItems,
    resultItems: matched,
    matchCount: matched.length,
    totalCount: sessions.length,
    page,
    pageCount,
    firstIndex: pageItems.length === 0 ? 0 : start + 1,
    lastIndex: pageItems.length === 0 ? 0 : start + pageItems.length,
    filtered:
      query.game !== ANY_GAME ||
      query.range !== 'any' ||
      query.search.trim().length > 0 ||
      query.favoriteOnly,
  };
}
