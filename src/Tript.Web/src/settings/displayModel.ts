// SPDX-License-Identifier: GPL-2.0-or-later
//
// The monitor-selection model: what the capture page's display picker offers, and what a fallback
// warning says. Pure, and kept out of the components, because every interesting case here is the
// machine changing under a saved setting — a monitor that has been unplugged, a host that could not
// enumerate at all, a warning that must come back when a *different* monitor goes missing.

import type { SelectOption } from './form';
import type { DisplayFallbackWarning, DisplayInfo } from './settingsModel';

/** The `<select>` value standing for `capture.display === null`, i.e. "whatever is primary". */
export const PRIMARY_DISPLAY_VALUE = '__primary_display__';

export const PRIMARY_DISPLAY_LABEL = 'Primary monitor (automatic)';

/** Marks the saved monitor when it is not in `availableDisplays`. */
export const NOT_CONNECTED_SUFFIX = ' (not connected)';

/** Marks the monitor the platform reports as primary. */
export const PRIMARY_SUFFIX = ' (primary)';

/** How the display field can be rendered — see `displayFieldMode`. */
export type DisplayFieldMode = 'picker' | 'text' | 'none';

/** One option's label: `"<name> — <width>x<height>"`, primary marked. A size the host did not report
 * is left off rather than shown as 0x0. */
export function displayOptionLabel(info: DisplayInfo): string {
  const size = info.width > 0 && info.height > 0 ? ` — ${info.width}x${info.height}` : '';
  return `${info.name}${size}${info.primary ? PRIMARY_SUFFIX : ''}`;
}

/**
 * Which control the display field is. The three states are genuinely different and collapsing them
 * would produce a dropdown that cannot be used:
 *   - `picker` — monitors were enumerated, offer them;
 *   - `text`   — the host could not enumerate (null), so fall back to typing an id, which is the
 *                only way the setting stays reachable on a host whose enumeration is broken;
 *   - `none`   — enumerated and found nothing (an empty array). An empty dropdown is not a control;
 *                say so instead.
 */
export function displayFieldMode(displays: DisplayInfo[] | null): DisplayFieldMode {
  if (displays === null) {
    return 'text';
  }
  return displays.length > 0 ? 'picker' : 'none';
}

/** The `<select>` value for a saved preference. */
export function displaySelectValue(display: string | null | undefined): string {
  return display ? display : PRIMARY_DISPLAY_VALUE;
}

/**
 * The picker's options: the primary sentinel, every attached monitor, and — when the saved id is not
 * among them — the saved id itself, labelled from `capture.displayLabel`. That last option is the
 * point of `displayLabel` existing: a `<select>` whose value matches no option renders BLANK, which
 * reads as a broken control rather than as "the monitor you chose is unplugged".
 */
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

/**
 * The settings patch for a pick. Both keys go in ONE patch: the id is what the recorder matches on,
 * the label exists only so a warning can name a monitor that is no longer attached.
 */
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

/**
 * `availableDisplays` off a settings push. `null` (absent, null, or a shape we do not recognise)
 * means "the host could not enumerate"; an array means it did, and an empty one is a real answer.
 */
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

/** `displayFallbackWarning` off a settings push, or null when there is no usable warning. */
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

/** What the warning banner says: the monitor that is missing, and the one being recorded instead. */
export function formatFallbackWarning(warning: DisplayFallbackWarning): string {
  const requested = warning.requestedLabel ?? warning.requestedId;
  const using = warning.usingLabel ?? warning.usingId;
  const instead = using ? `“${using}”` : 'the primary monitor';
  return `The monitor “${requested}” is not connected. Recording is using ${instead}. Your choice is kept and will be used again when that monitor is back.`;
}

/**
 * Whether the banner shows. Dismissal is keyed on `requestedId`, not a single boolean, so dismissing
 * one warning does not suppress a later one about a different monitor.
 */
export function shouldShowFallbackWarning(
  warning: DisplayFallbackWarning | null,
  dismissedRequestedId: string | null,
): boolean {
  return warning !== null && warning.requestedId !== dismissedRequestedId;
}
