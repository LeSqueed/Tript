// SPDX-License-Identifier: GPL-2.0-or-later

import type { ContentType } from '../ipc/protocol';
import { formatTime } from './player/timelineModel';

export function contentTypeLabel(contentType: ContentType): string {
  switch (contentType) {
    case 'clip':
      return 'Clip';
    case 'highlight':
      return 'Highlight';
    case 'buffer':
      return 'Buffer';
    default:
      return 'Recording';
  }
}

export function contentLabel(title: string | undefined, fileName: string): string {
  const trimmed = title?.trim();
  return trimmed && trimmed.length > 0 ? trimmed : fileName;
}

export function formatContentDuration(seconds: number | undefined): string | null {
  return typeof seconds === 'number' && Number.isFinite(seconds) && seconds > 0
    ? formatTime(seconds)
    : null;
}

export function formatContentSize(bytes: number | undefined): string | null {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes <= 0) {
    return null;
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  const rounded = unit === 0 || value >= 10 ? Math.round(value) : Math.round(value * 10) / 10;
  return `${rounded} ${units[unit]}`;
}
