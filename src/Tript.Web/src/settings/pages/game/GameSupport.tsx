// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import type { GameAddRequestedMessage, GameModelStatus } from '../../../ipc/protocol';
import { Button } from '../../../components/ui/controls';
import { useToast } from '../../../components/ui/toast/ToastProvider';

type RequestState = 'pending' | 'accepted' | 'alreadyRequested' | 'rateLimited';

const DEFAULT_RETRY_HOURS = 6;

export interface GameRequests {
  requestState: Record<string, RequestState>;
  requestGame: (gameId: string) => void;
}

export function useGameRequests(
  gameAddRequested: GameAddRequestedMessage | null,
  onRequestGame: (requestId: string, gameId: string) => void,
): GameRequests {
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

  return { requestState, requestGame };
}

function progressLabel(status: GameModelStatus): string {
  if (status.stage === 'checking') return 'Checking for a model…';
  if (status.stage === 'downloading' && status.totalBytes && status.completedBytes !== undefined) {
    return `Downloading model ${Math.floor((status.completedBytes / status.totalBytes) * 100)}%`;
  }
  return 'Installing model…';
}

export function GameSupportTag({ status }: { status?: GameModelStatus }) {
  if (!status) return null;
  if (status.stage === 'unsupported') {
    return <span className="pill pill-muted" data-testid="game-unsupported">Not supported yet</span>;
  }
  if (status.stage === 'error') {
    return <span className="pill game-support-error" data-testid="game-model-error" title={status.message}>Model download failed</span>;
  }
  if (status.stage === 'ready') {
    return <span className="pill game-support-pill" data-testid="game-supported" title={status.message}>Supported</span>;
  }
  return <span className="pill pill-muted" data-testid="game-model-progress">{progressLabel(status)}</span>;
}

export function GameSupportAction({
  gameId,
  status,
  requests,
  onDownloadModel,
}: {
  gameId: string;
  status?: GameModelStatus;
  requests: GameRequests;
  onDownloadModel: (gameId: string) => void;
}) {
  if (status?.stage === 'error') {
    return (
      <Button onClick={() => onDownloadModel(gameId)} title={status.message}>
        Download model
      </Button>
    );
  }

  if (status?.stage !== 'unsupported') return null;

  const state = requests.requestState[gameId];
  const label = state === 'pending' ? 'Requesting…'
    : state === 'accepted' || state === 'alreadyRequested' ? 'Requested'
      : 'Request this game';
  return (
    <Button
      variant="ghost"
      onClick={() => requests.requestGame(gameId)}
      disabled={state === 'pending' || state === 'accepted' || state === 'alreadyRequested'}
      title="This game has no detection model yet. Ask for one to be added."
    >
      {label}
    </Button>
  );
}
