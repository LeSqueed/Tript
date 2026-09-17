// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import type { GameAddRequestedMessage, GameInfo, GameModelStatus } from '../../../ipc/protocol';
import { Button } from '../../../components/ui/controls';
import { useToast } from '../../../components/ui/toast/ToastProvider';

type RequestState = 'pending' | 'accepted' | 'alreadyRequested' | 'rateLimited';

const DEFAULT_RETRY_HOURS = 6;

export function UnsupportedGamesList({
  modelStatuses,
  catalogueGames,
  gameAddRequested,
  onRequestGame,
}: {
  modelStatuses: GameModelStatus[];
  catalogueGames: GameInfo[];
  gameAddRequested: GameAddRequestedMessage | null;
  onRequestGame: (requestId: string, gameId: string) => void;
}) {
  const [requestState, setRequestState] = useState<Record<string, RequestState>>({});
  const pendingRequests = useRef<Map<string, string>>(new Map());
  const toast = useToast();

  useEffect(() => {
    if (!gameAddRequested) return;
    const gameId = pendingRequests.current.get(gameAddRequested.requestId);
    if (!gameId) return;
    pendingRequests.current.delete(gameAddRequested.requestId);

    if (gameAddRequested.status === 'rejected') {
      setRequestState((current) => {
        const { [gameId]: _dropped, ...rest } = current;
        return rest;
      });
      toast.push({ kind: 'error', message: gameAddRequested.error ?? 'The request could not be sent.' });
      return;
    }

    setRequestState((current) => ({ ...current, [gameId]: gameAddRequested.status as RequestState }));
    if (gameAddRequested.status === 'accepted') {
      toast.push({ kind: 'success', message: 'Request received. Thanks!' });
    } else if (gameAddRequested.status === 'alreadyRequested') {
      toast.push({ kind: 'info', message: "You've already requested this game." });
    } else if (gameAddRequested.status === 'rateLimited') {
      const hours = gameAddRequested.retryAfterSeconds
        ? Math.max(1, Math.round(gameAddRequested.retryAfterSeconds / 3600))
        : DEFAULT_RETRY_HOURS;
      toast.push({ kind: 'warning', message: `Too many requests. Try again in about ${hours} hour${hours === 1 ? '' : 's'}.` });
    }
  }, [gameAddRequested, toast]);

  function requestGame(gameId: string) {
    const requestId = crypto.randomUUID();
    pendingRequests.current.set(requestId, gameId);
    setRequestState((current) => ({ ...current, [gameId]: 'pending' }));
    onRequestGame(requestId, gameId);
  }

  const unsupportedGames = modelStatuses.filter((status) => status.stage === 'unsupported');
  if (unsupportedGames.length === 0) {
    return null;
  }

  return (
    <div className="game-list unsupported-games" data-testid="unsupported-games">
      <div className="game-list-heading">
        <h3 className="subheading">Unsupported games</h3>
      </div>
      <p className="muted small">
        These games don't have a detection model yet. You can ask for one to be added.
      </p>
      {unsupportedGames.map((status) => {
        const name = catalogueGames.find((game) => game.id === status.gameId)?.name ?? status.gameId;
        const state = requestState[status.gameId];
        const label = state === 'pending' ? 'Requesting…'
          : state === 'accepted' || state === 'alreadyRequested' ? 'Requested'
            : 'Request this game';
        return (
          <div className="game-row" key={status.gameId} data-testid="unsupported-game-row">
            <div className="game-row-main">
              <strong>{name}</strong>
              <span className="muted small">{status.gameId}</span>
              <Button
                variant="ghost"
                onClick={() => requestGame(status.gameId)}
                disabled={state === 'pending' || state === 'accepted' || state === 'alreadyRequested'}
              >
                {label}
              </Button>
            </div>
          </div>
        );
      })}
    </div>
  );
}
