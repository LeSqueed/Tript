// SPDX-License-Identifier: GPL-2.0-or-later
//
// Whether the window is too narrow for the full-size player route. Below this the player opens as
// the overlay instead — the same layer the library has always used for a quick look.

import { useEffect, useState } from 'react';

/** Below this the player's console and both timelines stop fitting beside the nav rail. */
export const COMPACT_QUERY = '(max-width: 1100px)';

export function useCompactLayout(query: string = COMPACT_QUERY): boolean {
  const [compact, setCompact] = useState(() => window.matchMedia(query).matches);

  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = (event: MediaQueryListEvent) => setCompact(event.matches);
    list.addEventListener('change', onChange);
    // Re-read on subscribe: the width can change between first render and this effect.
    setCompact(list.matches);
    return () => list.removeEventListener('change', onChange);
  }, [query]);

  return compact;
}
