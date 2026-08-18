// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings — logical pages: recording, buffer/replay, audio, capture, game. The buffer has its own
// page; the audio page drives the multi-track model (track count, source→track routing, per-source
// volume).

import { useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import { useSettings, type SettingsPageName } from '../settings/useSettings';
import { RecordingPage } from '../settings/pages/RecordingPage';
import { BufferPage } from '../settings/pages/BufferPage';
import { AudioPage } from '../settings/pages/AudioPage';
import { CapturePage } from '../settings/pages/CapturePage';
import { GamePage } from '../settings/pages/GamePage';

const PAGES: { id: SettingsPageName; label: string }[] = [
  { id: 'recording', label: 'Recording' },
  { id: 'buffer', label: 'Buffer' },
  { id: 'audio', label: 'Audio' },
  { id: 'capture', label: 'Capture' },
  { id: 'game', label: 'Game' },
];

export function SettingsView({ client }: { client: IpcClient }) {
  const [page, setPage] = useState<SettingsPageName>('recording');
  const controller = useSettings(client);

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
        {!controller.hasSettings && (
          <p className="muted small">
            Waiting for the backend to push settings. The forms stay editable; changes are sent
            when the connection is live.
          </p>
        )}
        {page === 'recording' && (
          <RecordingPage
            settings={controller.settings.recording}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
            availableEncoders={controller.availableEncoders}
            displayResolution={controller.displayResolution}
            onBrowse={() => client.send('SetVideoLocation')}
          />
        )}
        {page === 'buffer' && (
          <BufferPage
            settings={controller.settings.buffer}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
          />
        )}
        {page === 'audio' && (
          <AudioPage settings={controller.settings.audio} update={controller.update} page={page} />
        )}
        {page === 'capture' && (
          <CapturePage
            settings={controller.settings.capture}
            update={controller.update}
            page={page}
            availableDisplays={controller.availableDisplays}
          />
        )}
        {page === 'game' && (
          <GamePage
            settings={controller.settings.game}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
          />
        )}
      </div>
    </section>
  );
}
