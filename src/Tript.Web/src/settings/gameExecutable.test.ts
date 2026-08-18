// SPDX-License-Identifier: GPL-2.0-or-later
//
// Clearing the field must send null, not ''. The backend falls back to the game's name only when the
// executable is absent, so an empty string would be taken as the process name to watch for — and the
// recorder would attach game capture to nothing at all, silently.

import { describe, expect, it } from 'vitest';
import { executablePatch } from './gameExecutable';

describe('executablePatch', () => {
  it('sends the typed process name, trimmed', () => {
    expect(executablePatch('cs2')).toEqual({ executable: 'cs2' });
    expect(executablePatch('  cs2  ')).toEqual({ executable: 'cs2' });
  });

  // Whitespace is not a process name, and it would defeat the fallback just as an empty string does.
  it('sends null for a cleared or blank field, so the name takes over again', () => {
    expect(executablePatch('')).toEqual({ executable: null });
    expect(executablePatch('   ')).toEqual({ executable: null });
  });
});
