import { useEffect } from 'react';
import type { GameAddedMessage } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { useToast } from '../ui/toast/ToastProvider';

export function GameAddedToasts({
  client,
  onOpenGameSettings,
}: {
  client: IpcClient;
  onOpenGameSettings: (gameId: string) => void;
}) {
  const { push } = useToast();

  useEffect(() => client.on('gameAdded', (content) => {
    const added = content as Partial<GameAddedMessage> | null;
    if (!added || typeof added.gameId !== 'string' || typeof added.name !== 'string'
      || typeof added.executablePath !== 'string') return;

    push({
      key: `game-added-${added.gameId}`,
      kind: 'info',
      duration: 0,
      message: `${added.name} was added to your games.`,
      actions: [{ label: 'Go to settings', onClick: () => onOpenGameSettings(added.gameId!) }],
    });
  }), [client, push, onOpenGameSettings]);

  return null;
}
