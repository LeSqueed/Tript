// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { DisplayInfo } from './settingsModel';
import {
  buildDisplayOptions,
  displayFieldMode,
  displayOptionLabel,
  displaySelectValue,
  displaySelectionPatch,
  formatFallbackWarning,
  NOT_CONNECTED_SUFFIX,
  PRIMARY_DISPLAY_LABEL,
  PRIMARY_DISPLAY_VALUE,
  readAvailableDisplays,
  readDisplayFallbackWarning,
  shouldShowFallbackWarning,
} from './displayModel';

const DISPLAYS: DisplayInfo[] = [
  { id: 'monitor-1', name: 'DP-1', width: 2560, height: 1440, primary: true },
  { id: 'monitor-2', name: 'HDMI-A-1', width: 1920, height: 1080, primary: false },
];

describe('option labels', () => {
  it('is "<name>: <width>x<height>", with the primary marked', () => {
    expect(displayOptionLabel(DISPLAYS[1])).toBe('HDMI-A-1: 1920x1080');
    expect(displayOptionLabel(DISPLAYS[0])).toBe('DP-1: 2560x1440 (primary)');
  });

  it('leaves the size off when the host reported none', () => {
    expect(displayOptionLabel({ id: 'x', name: 'DP-9', width: 0, height: 0, primary: false })).toBe('DP-9');
  });
});

describe('buildDisplayOptions', () => {
  it('offers the primary sentinel first, then every attached monitor', () => {
    const options = buildDisplayOptions(DISPLAYS, null);
    expect(options.map((option) => option.value)).toEqual([
      PRIMARY_DISPLAY_VALUE,
      'monitor-1',
      'monitor-2',
    ]);
    expect(options[0].label).toBe(PRIMARY_DISPLAY_LABEL);
  });

  it('keeps a saved monitor that is no longer attached selectable, labelled from displayLabel', () => {
    const options = buildDisplayOptions(DISPLAYS, 'monitor-gone', 'DP-3');
    expect(options.map((option) => option.value)).toContain('monitor-gone');
    expect(options[options.length - 1].label).toBe(`DP-3${NOT_CONNECTED_SUFFIX}`);
  });

  it('falls back to the raw id when no label was ever saved for it', () => {
    const options = buildDisplayOptions(DISPLAYS, 'monitor-gone', null);
    expect(options[options.length - 1].label).toBe(`monitor-gone${NOT_CONNECTED_SUFFIX}`);
  });

  it('does not duplicate a saved monitor that is attached', () => {
    const options = buildDisplayOptions(DISPLAYS, 'monitor-2', 'HDMI-A-1');
    expect(options.filter((option) => option.value === 'monitor-2')).toHaveLength(1);
  });

  it('every stored value has a matching option, so the select is never blank', () => {
    for (const stored of [null, 'monitor-1', 'monitor-gone']) {
      const options = buildDisplayOptions(DISPLAYS, stored, 'DP-3');
      expect(options.some((option) => option.value === displaySelectValue(stored))).toBe(true);
    }
  });
});

describe('displaySelectionPatch', () => {
  it('writes the id and the name together for an attached monitor', () => {
    expect(displaySelectionPatch('monitor-2', DISPLAYS)).toEqual({
      display: 'monitor-2',
      displayLabel: 'HDMI-A-1',
    });
  });

  it('clears both when the primary sentinel is picked', () => {
    expect(displaySelectionPatch(PRIMARY_DISPLAY_VALUE, DISPLAYS, 'DP-1')).toEqual({
      display: null,
      displayLabel: null,
    });
  });

  it('keeps the saved label when the not-connected option is re-picked', () => {
    expect(displaySelectionPatch('monitor-gone', DISPLAYS, 'DP-3')).toEqual({
      display: 'monitor-gone',
      displayLabel: 'DP-3',
    });
  });
});

describe('displayFieldMode', () => {
  it('separates "could not enumerate" (null) from "enumerated, none found" ([])', () => {
    expect(displayFieldMode(null)).toBe('text');
    expect(displayFieldMode([])).toBe('none');
    expect(displayFieldMode(DISPLAYS)).toBe('picker');
  });
});

describe('readAvailableDisplays', () => {
  it('reads a list, defaulting the fields the host left out', () => {
    expect(readAvailableDisplays([{ id: 'a' }, { id: 'b', name: 'DP-2', width: 800, height: 600, primary: true }])).toEqual([
      { id: 'a', name: 'a', width: 0, height: 0, primary: false },
      { id: 'b', name: 'DP-2', width: 800, height: 600, primary: true },
    ]);
  });

  it('keeps an empty array as an empty array: it is an answer, not a failure', () => {
    expect(readAvailableDisplays([])).toEqual([]);
  });

  it('is null for anything that is not a list', () => {
    expect(readAvailableDisplays(null)).toBeNull();
    expect(readAvailableDisplays(undefined)).toBeNull();
    expect(readAvailableDisplays('DP-1')).toBeNull();
    expect(readAvailableDisplays({ id: 'a' })).toBeNull();
  });

  it('drops entries with no usable id rather than offering an unpickable option', () => {
    expect(readAvailableDisplays([{ name: 'DP-1' }, { id: '' }, null, 3])).toEqual([]);
  });
});

describe('readDisplayFallbackWarning', () => {
  it('reads a warning and normalises the optional names to null', () => {
    expect(readDisplayFallbackWarning({ requestedId: 'gone', usingId: 'monitor-1' })).toEqual({
      requestedId: 'gone',
      requestedLabel: null,
      usingId: 'monitor-1',
      usingLabel: null,
    });
  });

  it('is null without a requestedId: there is nothing to warn about', () => {
    expect(readDisplayFallbackWarning(null)).toBeNull();
    expect(readDisplayFallbackWarning({})).toBeNull();
    expect(readDisplayFallbackWarning({ requestedId: '' })).toBeNull();
  });
});

describe('formatFallbackWarning', () => {
  it('names the missing monitor and the one being used instead', () => {
    const text = formatFallbackWarning({
      requestedId: 'monitor-gone',
      requestedLabel: 'DP-3',
      usingId: 'monitor-1',
      usingLabel: 'DP-1',
    });
    expect(text).toContain('DP-3');
    expect(text).toContain('DP-1');
  });

  it('falls back to the ids, and says "the primary monitor" when the host cannot name one', () => {
    const text = formatFallbackWarning({
      requestedId: 'monitor-gone',
      requestedLabel: null,
      usingId: null,
      usingLabel: null,
    });
    expect(text).toContain('monitor-gone');
    expect(text).toContain('the primary monitor');
  });
});

describe('shouldShowFallbackWarning', () => {
  it('shows a warning until its own monitor is dismissed', () => {
    const warning = { requestedId: 'gone-1', requestedLabel: null, usingId: null, usingLabel: null };
    expect(shouldShowFallbackWarning(warning, null)).toBe(true);
    expect(shouldShowFallbackWarning(warning, 'gone-1')).toBe(false);
  });

  it('shows a warning about a different monitor even after one was dismissed', () => {
    const other = { requestedId: 'gone-2', requestedLabel: null, usingId: null, usingLabel: null };
    expect(shouldShowFallbackWarning(other, 'gone-1')).toBe(true);
  });

  it('shows nothing when there is no warning', () => {
    expect(shouldShowFallbackWarning(null, null)).toBe(false);
  });
});
