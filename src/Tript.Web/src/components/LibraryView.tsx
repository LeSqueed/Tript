// SPDX-License-Identifier: GPL-2.0-or-later
//
// The library / session list. In the alpha this is a stub list; the IPC round-trip proof lives in
// the live-connection panel below it. The IPC client is owned by the app shell and passed down.

import type { IpcClient } from '../ipc/websocketClient';

export function LibraryView({ client }: { client: IpcClient }) {
  return (
    <section className="panel">
      <h2>Library</h2>
      <p className="muted">Your recordings will appear here.</p>
      <LiveIpcProbe client={client} />
    </section>
  );
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
