// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library / session list. Sessions are read from the shared IPC session source (owned by the
// App shell), so the list is live: new recordings appear after StopRecording, deletes and renames
// are reflected. Clicking a session opens it in the player via the shell's `onOpen` seam.
//
// The IPC client is owned by the app shell and passed down.

import type { IpcClient } from '../ipc/websocketClient';
import type { ContentItem } from '../ipc/protocol';

export function LibraryView({
  client,
  sessions,
  onOpen,
}: {
  client: IpcClient;
  /** The session list, already filtered and reactive (owned by the App shell's source). */
  sessions: ContentItem[];
  /** The App shell's player seam: called with the session the user clicked. */
  onOpen?: (item: ContentItem) => void;
}) {
  return (
    <section className="panel library-view">
      <h2>Library</h2>
      {sessions.length === 0 ? (
        <p className="muted">Your recordings will appear here.</p>
      ) : (
        <ul className="content-list" data-testid="library-list">
          {sessions.map((item) => (
            <li key={item.filePath}>
              <button
                type="button"
                className="content-row"
                onClick={() => onOpen?.(item)}
                aria-label={`Open ${item.title ?? item.fileName}`}
              >
                <span className="content-name">{item.title ?? item.fileName}</span>
                <span className="content-time muted small">
                  {item.startTime !== undefined ? formatStartTime(item.startTime) : 'No start time'}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
      <LiveIpcProbe client={client} />
    </section>
  );
}

function formatStartTime(unixSeconds: number): string {
  return new Date(unixSeconds * 1000).toLocaleString();
}

/**
 * Live IPC round-trip proof: sends commands over the control socket and displays the state push.
 * Fails gracefully when the backend is not running (the connection badge shows it).
 */
function LiveIpcProbe({ client }: { client: IpcClient }) {
  return (
    <div className="live-probe">
      <h3>Connection probe</h3>
      <div className="probe-actions">
        <button type="button" className="btn" onClick={() => client.send('StartRecording')}>
          Start recording
        </button>
        <button type="button" className="btn" onClick={() => client.send('StopRecording')}>
          Stop recording
        </button>
        <button type="button" className="btn" onClick={() => client.send('CheckForUpdates')}>
          Check for updates
        </button>
      </div>
      <p className="muted small">
        If the backend is running, these send commands on the control socket and the state push
        appears in the recorder bar.
      </p>
    </div>
  );
}
