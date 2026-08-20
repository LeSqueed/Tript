// SPDX-License-Identifier: GPL-2.0-or-later
//
// The catalogue the detector watches, as names. Two surfaces need it and neither owns it: the
// recorder bar says what it is watching for, and the library's onboarding state says the same thing
// in its own words. Reading the push twice would have let them disagree.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';

export function useWatchedGames(client: IpcClient): string[] {
  const [games, setGames] = useState<string[]>([]);

  useEffect(() => {
    // Pushed as the raw list, so an entry is {id, name, ...}.
    return client.on('gameList', (content) => {
      if (Array.isArray(content)) {
        setGames(
          (content as { id?: string; name?: string }[])
            .map((game) => game.name || game.id)
            .filter((name): name is string => Boolean(name)),
        );
      }
    });
  }, [client]);

  return games;
}
