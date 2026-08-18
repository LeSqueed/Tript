// SPDX-License-Identifier: GPL-2.0-or-later
//
// The process name the recorder watches for, per game.
//
// It is not the display name often enough that guessing is wrong: "Counter-Strike 2" runs as `cs2`.
// The backend falls back to the name when the field is absent, so every settings file written before
// this existed keeps working — which means the frontend must write "absent" and not an empty string
// when the user clears the field, or the fallback is replaced by a process name of "".

import type { GameSetting } from './settingsModel';

/** The edit to send for a typed executable. Blank is `null` — "no override", not "watch for ''". */
export function executablePatch(input: string): Pick<GameSetting, 'executable'> {
  const trimmed = input.trim();
  return { executable: trimmed === '' ? null : trimmed };
}
