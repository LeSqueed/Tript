// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings — logical pages: recording, buffer/replay, audio, capture, game. The alpha ships the
// page tabs and one probe that sends UpdateSettings to the backend.

import { useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';

export type SettingsPage = 'recording' | 'buffer' | 'audio' | 'capture' | 'game';

const PAGES: { id: SettingsPage; label: string }[] = [
  { id: 'recording', label: 'Recording' },
  { id: 'buffer', label: 'Buffer' },
  { id: 'audio', label: 'Audio' },
  { id: 'capture', label: 'Capture' },
  { id: 'game', label: 'Game' },
];

export function SettingsView({ client }: { client: IpcClient }) {
  const [page, setPage] = useState<SettingsPage>('recording');

  return (
    <section className="settings-view">
      <div className="settings-tabs" role="tablist">
        {PAGES.map((p) => (
          <button
            key={p.id}
            type="button"
            role="tab"
            aria-selected={page === p.id}
            className={page === p.id ? 'settings-tab active' : 'settings-tab'}
            onClick={() => setPage(p.id)}
          >
            {p.label}
          </button>
        ))}
      </div>
      <div className="settings-body">
        <h2>{PAGES.find((p) => p.id === page)?.label}</h2>
        <p className="muted">Settings page content lands in the settings task.</p>
        {page === 'recording' && (
          <button
            type="button"
            className="btn"
            onClick={() => client.send('UpdateSettings', { settings: { recordingMode: 'manual' } })}
          >
            Apply sample setting
          </button>
        )}
      </div>
    </section>
  );
}
