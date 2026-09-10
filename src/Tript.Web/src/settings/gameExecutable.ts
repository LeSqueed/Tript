// SPDX-License-Identifier: GPL-2.0-or-later

import type { GameSetting } from './settingsModel';

export function executablePatch(input: string): Pick<GameSetting, 'executable'> {
  const trimmed = input.trim();
  return { executable: trimmed === '' ? null : trimmed };
}
