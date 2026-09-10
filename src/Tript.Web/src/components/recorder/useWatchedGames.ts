// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';

export function useWatchedGames(client: IpcClient): string[] {
  const [games, setGames] = useState<string[]>([]);

  useEffect(() => {
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
