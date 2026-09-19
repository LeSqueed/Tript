// SPDX-License-Identifier: GPL-2.0-or-later

import type {
  StorageGameUsage,
  StoragePressure,
  StorageReportMessage,
  StorageStatusMessage,
} from '../../ipc/protocol';
import type { StorageFullAction } from '../../settings/settingsModel';
import { formatContentSize } from '../contentPresentation';

export const GIGABYTE = 1024 * 1024 * 1024;

const PRESSURES = new Set<string>(['unknown', 'ok', 'warning', 'critical']);

const FULL_ACTIONS = new Set<string>(['PauseRecording', 'ReclaimOldest']);

export type StorageSegmentKind =
  | 'sessions'
  | 'highlights'
  | 'clips'
  | 'trash'
  | 'other'
  | 'elsewhere'
  | 'free';

export interface StorageSegment {
  kind: StorageSegmentKind;
  label: string;
  bytes: number;
  share: number;
}

export function formatStorageSize(bytes: number | undefined): string {
  return formatContentSize(bytes) ?? '0 B';
}

export function toGigabytes(bytes: number): number {
  return Math.round((bytes / GIGABYTE) * 10) / 10;
}

export function fromGigabytes(gigabytes: number): number {
  return Math.round(gigabytes * GIGABYTE);
}

function readNumber(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : 0;
}

export function parseStorageStatus(content: unknown): StorageStatusMessage | null {
  if (!content || typeof content !== 'object') {
    return null;
  }
  const value = content as Partial<StorageStatusMessage>;
  if (typeof value.pressure !== 'string' || !PRESSURES.has(value.pressure)) {
    return null;
  }
  const whenFull = typeof value.whenFull === 'string' && FULL_ACTIONS.has(value.whenFull)
    ? (value.whenFull as StorageFullAction)
    : 'PauseRecording';
  return {
    pressure: value.pressure as StoragePressure,
    freeBytes: readNumber(value.freeBytes),
    totalBytes: readNumber(value.totalBytes),
    minimumFreeBytes: readNumber(value.minimumFreeBytes),
    warnFreeBytes: readNumber(value.warnFreeBytes),
    recordingBlocked: value.recordingBlocked === true,
    policyConfirmed: value.policyConfirmed === true,
    whenFull,
    keepSharingWhenFull: value.keepSharingWhenFull === true,
    volumeRoot: typeof value.volumeRoot === 'string' ? value.volumeRoot : undefined,
    root: typeof value.root === 'string' ? value.root : '',
    scratchRoot: typeof value.scratchRoot === 'string' ? value.scratchRoot : undefined,
    scratchFreeBytes: readNumber(value.scratchFreeBytes),
    scratchLow: value.scratchLow === true,
  };
}

export function parseStorageReport(content: unknown): StorageReportMessage | null {
  if (!content || typeof content !== 'object') {
    return null;
  }
  const value = content as Partial<StorageReportMessage>;
  if (typeof value.root !== 'string') {
    return null;
  }
  return {
    root: value.root,
    volumeRoot: typeof value.volumeRoot === 'string' ? value.volumeRoot : undefined,
    volumeTotalBytes: readNumber(value.volumeTotalBytes),
    volumeFreeBytes: readNumber(value.volumeFreeBytes),
    libraryBytes: readNumber(value.libraryBytes),
    sessionBytes: readNumber(value.sessionBytes),
    highlightBytes: readNumber(value.highlightBytes),
    clipBytes: readNumber(value.clipBytes),
    trashBytes: readNumber(value.trashBytes),
    sidecarBytes: readNumber(value.sidecarBytes),
    favoriteBytes: readNumber(value.favoriteBytes),
    sessionCount: readNumber(value.sessionCount),
    highlightCount: readNumber(value.highlightCount),
    clipCount: readNumber(value.clipCount),
    trashCount: readNumber(value.trashCount),
    games: Array.isArray(value.games) ? (value.games as StorageGameUsage[]) : [],
  };
}

export function storageSegments(report: StorageReportMessage): StorageSegment[] {
  const elsewhere = Math.max(
    0,
    report.volumeTotalBytes - report.volumeFreeBytes - report.libraryBytes,
  );
  const scale = report.volumeTotalBytes > 0
    ? report.volumeTotalBytes
    : report.libraryBytes + report.volumeFreeBytes;

  const raw: { kind: StorageSegmentKind; label: string; bytes: number }[] = [
    { kind: 'sessions', label: 'Sessions', bytes: report.sessionBytes },
    { kind: 'highlights', label: 'Highlights', bytes: report.highlightBytes },
    { kind: 'clips', label: 'Clips', bytes: report.clipBytes },
    { kind: 'trash', label: 'Trash', bytes: report.trashBytes },
    { kind: 'other', label: 'Thumbnails and working files', bytes: report.sidecarBytes },
    { kind: 'elsewhere', label: 'Other data on this drive', bytes: elsewhere },
    { kind: 'free', label: 'Free', bytes: report.volumeFreeBytes },
  ];

  return raw.map((segment) => ({
    ...segment,
    share: scale > 0 ? segment.bytes / scale : 0,
  }));
}

export function gameUsageLabel(usage: StorageGameUsage): string {
  return usage.name ?? 'Not linked to a game';
}
