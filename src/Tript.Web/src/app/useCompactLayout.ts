// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';

export const COMPACT_QUERY = '(max-width: 1100px)';

export function useCompactLayout(query: string = COMPACT_QUERY): boolean {
  const [compact, setCompact] = useState(() => window.matchMedia(query).matches);

  useEffect(() => {
    const list = window.matchMedia(query);
    const onChange = (event: MediaQueryListEvent) => setCompact(event.matches);
    list.addEventListener('change', onChange);
    setCompact(list.matches);
    return () => list.removeEventListener('change', onChange);
  }, [query]);

  return compact;
}
