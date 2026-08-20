// SPDX-License-Identifier: GPL-2.0-or-later
//
// The recorder bar — always visible across the shell. Shows the record state, the detected game,
// audio routing status, and the live IPC connection state. A live proof of the round trip: the
// state badge reflects the `state` push from the backend.

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { ConnectionState } from '../ipc/websocketClient';
import type { RecordingState } from '../ipc/protocol';

/** The `state` message content on the wire: the recording state plus the change cause. */
interface StateMessageContent {
  state: RecordingState;
  cause?: string;
}

export function RecorderBar({
  client,
  connectionState,
}: {
  client: IpcClient;
  connectionState: ConnectionState;
}) {
  const [recordingState, setRecordingState] = useState<RecordingState | null>(null);

  useEffect(() => {
    return client.on('state', (content) => {
      const message = content as StateMessageContent;
      const state = message?.state;
      if (state && typeof state === 'object' && 'recording' in state) {
        setRecordingState(state);
      }
    });
  }, [client]);

  const recording = recordingState?.recording ?? false;
  const canControl = connectionState === 'connected';
  const gameName = recordingState?.game?.name ?? recordingState?.game?.id ?? 'Unknown game';

  return (
    <header className="recorder-bar">
      <span className={`rec-dot ${recording ? 'recording' : ''}`} />
      <span className="rec-label">{recording ? 'Recording' : 'Stopped'}</span>
      <span className="rec-sep">·</span>
      <span className="rec-game" title="Detected game">
        {recordingState ? gameName : 'No game detected'}
      </span>
      {recordingState?.audioTracks && recordingState.audioTracks.length > 0 && (
        <>
          <span className="rec-sep">·</span>
          <span className="rec-audio" title="Audio routing">
            {recordingState.audioTracks.length} track{recordingState.audioTracks.length === 1 ? '' : 's'}
          </span>
        </>
      )}
      <div className="rec-actions" aria-label="Recording controls">
        {!recording ? (
          <button
            type="button"
            className="rec-action rec-action-primary"
            onClick={() => client.send('StartRecording')}
            disabled={!canControl}
            title={canControl ? 'Start recording' : 'Waiting for the capture host'}
          >
            Start capture
          </button>
        ) : (
          <button
            type="button"
            className="rec-action rec-action-stop"
            onClick={() => client.send('StopRecording')}
            disabled={!canControl}
            title={canControl ? 'Stop recording' : 'Waiting for the capture host'}
          >
            Stop capture
          </button>
        )}
      </div>
      <span className="rec-connection">
        <ConnectionBadge state={connectionState} />
      </span>
    </header>
  );
}

function ConnectionBadge({ state }: { state: ConnectionState }) {
  return (
    <span className={`conn-badge conn-${state}`} data-testid="connection-state">
      {state === 'connected' ? 'Connected' : state === 'connecting' ? 'Connecting…' : 'Disconnected'}
    </span>
  );
}

