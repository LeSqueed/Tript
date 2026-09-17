// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { hasOverrides } from './GameOverrideFields';

describe('hasOverrides', () => {
  const base = { id: 'game', name: 'Game' };

  it('is false for a game that only has its identity', () => {
    expect(hasOverrides(base)).toBe(false);
    expect(hasOverrides({
      ...base,
      autoRecordOverride: null,
      qualityOverride: { fps: null, encoder: null },
      automaticClipOverride: { beforeSeconds: null, afterSeconds: null },
    })).toBe(false);
  });

  it.each([
    { autoRecordOverride: false },
    { recordingModeOverride: { mode: 'Session' as const } },
    { captureMethodOverride: { method: 'Game' as const } },
    { qualityOverride: { fps: 60 } },
    { qualityOverride: { encoder: 'obs_x264' } },
    { automaticClipOverride: { beforeSeconds: null, afterSeconds: 4 } },
  ])('is true when %o is set', (override) => {
    expect(hasOverrides({ ...base, ...override })).toBe(true);
  });
});
