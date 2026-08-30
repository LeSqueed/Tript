// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { GameCandidateActionResultMessage, GameCandidateMessage } from '../ipc/protocol';
import { Button } from './ui/controls';

export function GameCandidateBanner({ client }: { client: IpcClient }) {
  const [candidate, setCandidate] = useState<GameCandidateMessage | null>(null);
  const [pending, setPending] = useState<{ requestId: string; executablePath: string; action: 'add' | 'ignore' } | null>(null);
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

  if (candidate === null) {
    return null;
  }

  const active = candidate;

  function addAsCustomGame() {
    const requestId = crypto.randomUUID();
    setPending({ requestId, executablePath: active.executablePath, action: 'add' });
    setError(null);
    client.send('AddGameCandidate', {
      requestId,
      name: active.executable,
      executablePath: active.executablePath,
    });
  }

  function ignore() {
    const requestId = crypto.randomUUID();
    setPending({ requestId, executablePath: active.executablePath, action: 'ignore' });
    setError(null);
    client.send('IgnoreGameCandidate', { requestId, executablePath: active.executablePath });
  }

  return (
    <div className="error-banner game-candidate-banner" role="status">
      <span className="error-banner-message">
        <code>{active.executablePath}</code> is running fullscreen but is not a known game. Add it as a custom game?
      </span>
      {error && <span className="game-validation" role="alert">{error}</span>}
      <span className="game-candidate-actions">
        <Button onClick={addAsCustomGame} disabled={pending !== null}>Add as custom game</Button>
        <Button variant="ghost" onClick={ignore} disabled={pending !== null}>Ignore</Button>
      </span>
    </div>
  );
}
