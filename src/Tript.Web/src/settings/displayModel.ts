// SPDX-License-Identifier: GPL-2.0-or-later

import type { SelectOption } from '../components/ui/controls';
import type { DisplayFallbackWarning, DisplayInfo } from './settingsModel';

export const PRIMARY_DISPLAY_VALUE = '__primary_display__';

export const PRIMARY_DISPLAY_LABEL = 'Primary monitor (automatic)';

export const NOT_CONNECTED_SUFFIX = ' (not connected)';

export const PRIMARY_SUFFIX = ' (primary)';

export type DisplayFieldMode = 'picker' | 'text' | 'none';

export function displayOptionLabel(info: DisplayInfo): string {
  const size = info.width > 0 && info.height > 0 ? `: ${info.width}x${info.height}` : '';
  return `${info.name}${size}${info.primary ? PRIMARY_SUFFIX : ''}`;
}

export function displayFieldMode(displays: DisplayInfo[] | null): DisplayFieldMode {
  if (displays === null) {
    return 'text';
  }
  return displays.length > 0 ? 'picker' : 'none';
}

export function displaySelectValue(display: string | null | undefined): string {
  return display ? display : PRIMARY_DISPLAY_VALUE;
}

export function buildDisplayOptions(
  displays: DisplayInfo[],
  selectedId: string | null | undefined,
  savedLabel?: string | null,
): SelectOption[] {
  const options: SelectOption[] = [
    { value: PRIMARY_DISPLAY_VALUE, label: PRIMARY_DISPLAY_LABEL },
    ...displays.map((info) => ({ value: info.id, label: displayOptionLabel(info) })),
  ];
  if (selectedId && !displays.some((info) => info.id === selectedId)) {
    const name = savedLabel && savedLabel.trim().length > 0 ? savedLabel : selectedId;
    options.push({ value: selectedId, label: `${name}${NOT_CONNECTED_SUFFIX}` });
  }
  return options;
}

export function displaySelectionPatch(
  value: string,
  displays: DisplayInfo[],
  savedLabel?: string | null,
): { display: string | null; displayLabel: string | null } {
  if (value === PRIMARY_DISPLAY_VALUE) {
    return { display: null, displayLabel: null };
  }
  const picked = displays.find((info) => info.id === value);
  return { display: value, displayLabel: picked ? picked.name : (savedLabel ?? null) };
}

export function readAvailableDisplays(value: unknown): DisplayInfo[] | null {
  if (!Array.isArray(value)) {
    return null;
  }
  const displays: DisplayInfo[] = [];
  for (const entry of value) {
    if (!entry || typeof entry !== 'object') {
      continue;
    }
    const { id, name, width, height, primary } = entry as Record<string, unknown>;
    if (typeof id !== 'string' || id.length === 0) {
      continue;
    }
    displays.push({
      id,
      name: typeof name === 'string' && name.length > 0 ? name : id,
      width: typeof width === 'number' && Number.isFinite(width) && width > 0 ? Math.round(width) : 0,
      height: typeof height === 'number' && Number.isFinite(height) && height > 0 ? Math.round(height) : 0,
      primary: primary === true,
    });
  }
  return displays;
}

export function readDisplayFallbackWarning(value: unknown): DisplayFallbackWarning | null {
  if (!value || typeof value !== 'object') {
    return null;
  }
  const { requestedId, requestedLabel, usingId, usingLabel } = value as Record<string, unknown>;
  if (typeof requestedId !== 'string' || requestedId.length === 0) {
    return null;
  }
  return {
    requestedId,
    requestedLabel: typeof requestedLabel === 'string' && requestedLabel.length > 0 ? requestedLabel : null,
    usingId: typeof usingId === 'string' && usingId.length > 0 ? usingId : null,
    usingLabel: typeof usingLabel === 'string' && usingLabel.length > 0 ? usingLabel : null,
  };
}

export function formatFallbackWarning(warning: DisplayFallbackWarning): string {
  const requested = warning.requestedLabel ?? warning.requestedId;
  const using = warning.usingLabel ?? warning.usingId;
  const instead = using ? `“${using}”` : 'the primary monitor';
  return `The monitor “${requested}” is not connected. Recording is using ${instead}. Your choice is kept and will be used again when that monitor is back.`;
}

export function shouldShowFallbackWarning(
  warning: DisplayFallbackWarning | null,
  dismissedRequestedId: string | null,
): boolean {
  return warning !== null && warning.requestedId !== dismissedRequestedId;
}
