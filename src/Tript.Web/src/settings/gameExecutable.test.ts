// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { executablePatch } from './gameExecutable';

describe('executablePatch', () => {
  it('sends the typed process name, trimmed', () => {
    expect(executablePatch('cs2')).toEqual({ executable: 'cs2' });
    expect(executablePatch('  cs2  ')).toEqual({ executable: 'cs2' });
  });

  it('sends null for a cleared or blank field, so the name takes over again', () => {
    expect(executablePatch('')).toEqual({ executable: null });
    expect(executablePatch('   ')).toEqual({ executable: null });
  });
});
