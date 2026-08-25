// SPDX-License-Identifier: GPL-2.0-or-later
//
// The recording indicator, in the topbar. Its weight tracks what is actually happening: a live
// recording gets an elapsed clock and a stop control, an idle recorder gets a single button, and a
// dropped connection replaces the lot because nothing else on the bar can be acted on.
//
// Three things it deliberately does NOT show. A "Connected" badge, because connection is only
// information when it is broken. A game name when no game is detected — "Unknown game" is the app
// admitting it has nothing to say. And the word "Stopped", which the absent clock already says and
// the button already offers to change.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ConnectionState } from '../ipc/websocketClient';
import type { RecordingState } from '../ipc/protocol';
import { Button } from './ui/controls';
import { deriveRecorderState, formatElapsed } from './recorder/recorderState';

interface StateMessageContent {
  state: RecordingState;
  cause?: string;
}

export function RecorderBar({
  client,
  connectionState,
  /** Injectable clock so the elapsed time is testable without fake timers. */
  nowSeconds,
}: {
  client: IpcClient;
  connectionState: ConnectionState;
  nowSeconds?: number;
}) {
  const [recordingState, setRecordingState] = useState<RecordingState | null>(null);
  const [clipJobs, setClipJobs] = useState<Set<string>>(new Set());
  const [tick, setTick] = useState(() => Date.now() / 1000);

  useEffect(() => {
    return client.on('state', (content) => {
      const message = content as StateMessageContent;
      const state = message?.state;
      if (state && typeof state === 'object' && 'recording' in state) {
        setRecordingState(state);
      }
    });
  }, [client]);

  useEffect(() => {
    return client.on('importProgress', (content) => {
      const message = content as { id?: unknown; status?: unknown };
      if (typeof message.id !== 'string') {
        return;
      }
      setClipJobs((current) => {
        const next = new Set(current);
        if (message.status === 'importing') {
          next.add(message.id as string);
        } else if (message.status === 'done' || message.status === 'error') {
          next.delete(message.id as string);
        }
        return next;
      });
    });
  }, [client]);

  const recording = recordingState?.recording ?? false;
  const automaticClips = recordingState?.automaticClips;
  const creatingClips = clipJobs.size > 0 || automaticClips?.active === true;
  const clipStatus = automaticClips?.active
    ? automaticClips.paused
      ? `Highlights paused (${automaticClips.completed}/${automaticClips.total})`
      : `Creating highlights (${automaticClips.completed}/${automaticClips.total})`
    : 'Creating clips…';

  // One tick a second, and only while there is a clock to advance.
  useEffect(() => {
    if (!recording) {
      return;
    }
    const timer = setInterval(() => setTick(Date.now() / 1000), 1000);
    return () => clearInterval(timer);
  }, [recording]);

  const state = deriveRecorderState({
    connection: connectionState,
    recording,
    game: recordingState?.game?.name ?? recordingState?.game?.id ?? null,
    detected: recordingState?.game?.detected === true,
    startedAt: typeof recordingState?.startedAt === 'number' ? recordingState.startedAt : null,
  });

  if (state.kind === 'disconnected') {
    return (
      <div className="recorder-bar recorder-bar-error" data-testid="recorder-bar">
        <span className="rec-problem">⚠ Not connected</span>
      </div>
    );
  }

  if (state.kind === 'recording') {
    const now = nowSeconds ?? tick;
    return (
      <div className="recorder-bar" data-testid="recorder-bar">
        <span className="rec-dot recording" aria-hidden="true" />
        <span className="rec-elapsed" data-testid="recording-elapsed">
          {state.startedAt === null ? '—' : formatElapsed(now - state.startedAt)}
        </span>
        {state.game && <span className="rec-game">{state.game}</span>}
        {creatingClips && <span className="rec-activity" data-testid="clip-creation-status">{clipStatus}</span>}
        <Button variant="ghost" size="small" onClick={() => client.send('StopRecording')}>
          Stop
        </Button>
      </div>
    );
  }

  return (
    <div className="recorder-bar" data-testid="recorder-bar">
      {creatingClips && <span className="rec-activity" data-testid="clip-creation-status">{clipStatus}</span>}
      {state.kind === 'detected' && (
        <>
          <span className="rec-dot detected" aria-hidden="true" />
          <span className="rec-game">Detected: {state.game}</span>
        </>
      )}
      <Button
        variant="ghost"
        size="small"
        onClick={() => recordingState?.game?.detected
          ? client.send('StartRecording', { gameId: recordingState.game.id })
          : client.send('StartRecording')}
      >
        Record
      </Button>
    </div>
  );
}
