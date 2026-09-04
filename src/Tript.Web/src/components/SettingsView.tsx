// SPDX-License-Identifier: GPL-2.0-or-later
//
// Settings — logical pages: recording, highlights, general, audio, capture, games. The highlights
// page owns the replay buffer and the automatic-clip switch; the audio page drives the multi-track
// model (track count, source→track routing, per-source volume).

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { SelectedGameExecutableMessage } from '../ipc/protocol';
import { useSettings, type SettingsPageName } from '../settings/useSettings';
import { RecordingPage } from '../settings/pages/RecordingPage';
import { HighlightsPage } from '../settings/pages/HighlightsPage';
import { AudioPage } from '../settings/pages/AudioPage';
import { CapturePage } from '../settings/pages/CapturePage';
import { GamePage } from '../settings/pages/GamePage';
import { GeneralPage } from '../settings/pages/GeneralPage';

const PAGES: { id: SettingsPageName; label: string }[] = [
  { id: 'recording', label: 'Recording' },
  { id: 'buffer', label: 'Highlights' },
  { id: 'general', label: 'General' },
  { id: 'audio', label: 'Audio' },
  { id: 'capture', label: 'Capture' },
  { id: 'game', label: 'Games' },
];

export function SettingsView({ client, builtInGameIds = [] }: { client: IpcClient; builtInGameIds?: readonly string[] }) {
  const [page, setPage] = useState<SettingsPageName>('general');
  const [selectedGameExecutable, setSelectedGameExecutable] = useState<SelectedGameExecutableMessage | null>(null);
  const controller = useSettings(client);

  useEffect(() => client.on('selectedGameExecutable', (content) => {
    const selected = content as Partial<SelectedGameExecutableMessage> | null;
    if (typeof selected?.requestId === 'string' && (typeof selected.filePath === 'string' || selected.filePath === null)) {
      setSelectedGameExecutable(selected as SelectedGameExecutableMessage);
    }
  }), [client]);

  return (
    <section className="settings-view">
      <div className="settings-layout">
        <div className="settings-tabs" role="tablist" aria-label="Settings sections">
          {PAGES.map((p) => (
            <button
              key={p.id}
              type="button"
              role="tab"
              id={`settings-tab-${p.id}`}
              aria-controls="settings-panel"
              aria-selected={page === p.id}
              tabIndex={page === p.id ? 0 : -1}
              className={page === p.id ? 'settings-tab active' : 'settings-tab'}
              onClick={() => setPage(p.id)}
              onKeyDown={(event) => {
                if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp' && event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') {
                  return;
                }
                event.preventDefault();
                const current = PAGES.findIndex((candidate) => candidate.id === page);
                const direction = event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1;
                const next = PAGES[(current + direction + PAGES.length) % PAGES.length];
                setPage(next.id);
                requestAnimationFrame(() => document.getElementById(`settings-tab-${next.id}`)?.focus());
              }}
            >
              {p.label}
            </button>
          ))}
        </div>
        <div className="settings-body" id="settings-panel" role="tabpanel" aria-labelledby={`settings-tab-${page}`} tabIndex={0}>
          {!controller.hasSettings && (
            <p className="muted small">
              Waiting for the backend to push settings. The forms stay editable; changes are sent
              when the connection is live.
            </p>
          )}
          {page === 'recording' && (
          <RecordingPage
            settings={controller.settings.recording}
            buffer={controller.settings.buffer}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
            availableEncoders={controller.availableEncoders}
            displayResolution={controller.displayResolution}
            onBrowse={() => client.send('SetVideoLocation')}
          />
          )}
          {page === 'buffer' && (
          <HighlightsPage
            settings={controller.settings.buffer}
            recording={controller.settings.recording}
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
            game={controller.settings.game}
            update={controller.update}
            page={page}
            availableDisplays={controller.availableDisplays}
            externalPushCount={controller.externalPushCount}
          />
          )}
          {page === 'game' && (
          <GamePage
            settings={controller.settings.game}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
            builtInGameIds={builtInGameIds}
            selectedGameExecutable={selectedGameExecutable}
            settingsUpdateResult={controller.settingsUpdateResult}
            onBrowseExecutable={(requestId) => client.send('SelectGameExecutable', { requestId })}
            globalClipBeforeSeconds={controller.settings.recording.automaticClipBeforeSeconds}
            globalClipAfterSeconds={controller.settings.recording.automaticClipAfterSeconds}
            globalRecordingMode={controller.settings.recording.mode}
            automaticClipsEnabled={controller.settings.recording.automaticClipsEnabled === true}
          />
          )}
          {page === 'general' && (
          <GeneralPage
            settings={controller.settings.general}
            recording={controller.settings.recording}
            update={controller.update}
            page={page}
          />
          )}
        </div>
      </div>
    </section>
  );
}
