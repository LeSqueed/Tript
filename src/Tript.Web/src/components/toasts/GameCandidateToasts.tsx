// SPDX-License-Identifier: GPL-2.0-or-later

import { useCallback, useEffect, useState } from 'react';
import type { IpcClient } from '../../ipc/websocketClient';
import type { GameCandidateActionResultMessage, GameCandidateMessage } from '../../ipc/protocol';
import { useToast } from '../ui/toast/ToastProvider';

interface PendingAction {
  requestId: string;
  executablePath: string;
  action: 'add' | 'ignore';
}

export function GameCandidateToasts({ client }: { client: IpcClient }) {
  const { push, dismiss } = useToast();
  const [candidate, setCandidate] = useState<GameCandidateMessage | null>(null);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onCandidate = client.on('gameCandidate', (content) => {
      const parsed = content as Partial<GameCandidateMessage> | null;
      if (parsed && typeof parsed.executablePath === 'string' && typeof parsed.executable === 'string') {
        setPending(null);
        setError(null);
        setCandidate({
          pid: typeof parsed.pid === 'number' ? parsed.pid : 0,
          executable: parsed.executable,
          executablePath: parsed.executablePath,
          ...(typeof parsed.name === 'string' && parsed.name.trim() !== '' ? { name: parsed.name.trim() } : {}),
        });
      }
    });
    const onCleared = client.on('gameCandidateCleared', (content) => {
      const parsed = content as { executablePath?: unknown } | null;
      const clearedPath = parsed && typeof parsed.executablePath === 'string' ? parsed.executablePath : null;
      setCandidate((current) =>
        current && (clearedPath === null || clearedPath === current.executablePath) ? null : current);
    });
    const onResult = client.on('gameCandidateActionResult', (content) => {
      const result = content as Partial<GameCandidateActionResultMessage> | null;
      if (typeof result?.requestId !== 'string' || typeof result.executablePath !== 'string'
        || (result.action !== 'add' && result.action !== 'ignore') || typeof result.success !== 'boolean') {
        return;
      }
      setPending((current) => {
        if (!current || current.requestId !== result.requestId
          || current.executablePath !== result.executablePath || current.action !== result.action) {
          return current;
        }
        if (result.success) {
          setCandidate((active) => active?.executablePath === current.executablePath ? null : active);
          setError(null);
        } else {
          setError(typeof result.error === 'string' && result.error !== '' ? result.error : 'The action was rejected.');
        }
        return null;
      });
    });
    return () => {
      onCandidate();
      onCleared();
      onResult();
    };
  }, [client]);

  const addAsCustomGame = useCallback(() => {
    if (candidate === null) {
      return;
    }
    const requestId = crypto.randomUUID();
    setPending({ requestId, executablePath: candidate.executablePath, action: 'add' });
    setError(null);
    client.send('AddGameCandidate', {
      requestId,
      name: candidate.name ?? candidate.executable,
      executablePath: candidate.executablePath,
    });
  }, [candidate, client]);

  const ignore = useCallback(() => {
    if (candidate === null) {
      return;
    }
    const requestId = crypto.randomUUID();
    setPending({ requestId, executablePath: candidate.executablePath, action: 'ignore' });
    setError(null);
    client.send('IgnoreGameCandidate', { requestId, executablePath: candidate.executablePath });
  }, [candidate, client]);

  useEffect(() => {
    if (candidate === null) {
      dismiss('game-candidate');
      return;
    }
    push({
      key: 'game-candidate',
      kind: 'info',
      duration: 0,
      message: `${candidate.name ? `${candidate.name} (${candidate.executablePath})` : candidate.executablePath} is running fullscreen but is not a known game. Add it as a custom game?`,
      note: error ?? undefined,
      actions: [
        { label: 'Add as custom game', onClick: addAsCustomGame, disabled: pending !== null },
        { label: 'Ignore', variant: 'ghost', onClick: ignore, disabled: pending !== null },
      ],
    });
  }, [candidate, error, pending, push, dismiss, addAsCustomGame, ignore]);

  return null;
}
