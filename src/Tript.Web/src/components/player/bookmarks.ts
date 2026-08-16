// SPDX-License-Identifier: GPL-2.0-or-later
//
// Bookmark marker colours. The bookmark `type` is a free string on the wire; a small known
// vocabulary gets a stable colour, everything else falls back to the accent.

const BOOKMARK_COLORS: Record<string, string> = {
  kill: '#f87171',
  death: '#f0b429',
  goal: '#4aa8ff',
  assist: '#36d399',
  round: '#a78bfa',
  event: '#22d3ee',
};

export function bookmarkColor(type: string): string {
  return BOOKMARK_COLORS[type] ?? '#22d3ee';
}
