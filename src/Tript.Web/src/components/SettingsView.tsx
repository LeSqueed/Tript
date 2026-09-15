// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { AudioLevelsMessage, GameAddRequestedMessage, GameInfo, GameModelStatus, GameSearchResultsMessage, ModelStatusMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage } from '../ipc/protocol';
import { useSettings, type SettingsPageName } from '../settings/useSettings';
import { RecordingPage } from '../settings/pages/RecordingPage';
import { HighlightsPage } from '../settings/pages/HighlightsPage';
import { AudioPage } from '../settings/pages/AudioPage';
import { CapturePage } from '../settings/pages/CapturePage';
import { GamePage } from '../settings/pages/GamePage';
import { GeneralPage } from '../settings/pages/GeneralPage';
import { HotkeysPage } from '../settings/pages/HotkeysPage';

const PAGES: { id: SettingsPageName; label: string }[] = [
  { id: 'general', label: 'General' },
  { id: 'recording', label: 'Recording' },
  { id: 'buffer', label: 'Highlights' },
  { id: 'audio', label: 'Audio' },
  { id: 'capture', label: 'Capture' },
  { id: 'game', label: 'Games' },
  { id: 'hotkeys', label: 'Hotkeys' },
];

export function SettingsView({
  client,
  builtInGameIds = [],
  focusGameId = null,
  onFocusGameHandled,
}: {
  client: IpcClient;
  builtInGameIds?: readonly string[];
  focusGameId?: string | null;
  onFocusGameHandled?: () => void;
}) {
  const [page, setPage] = useState<SettingsPageName>('general');

  useEffect(() => {
    if (focusGameId) setPage('game');
  }, [focusGameId]);
  const [selectedGameExecutable, setSelectedGameExecutable] = useState<SelectedGameExecutableMessage | null>(null);
  const [gameSearchResults, setGameSearchResults] = useState<GameSearchResultsMessage | null>(null);
  const [resolvedGameSearch, setResolvedGameSearch] = useState<ResolvedGameSearchMessage | null>(null);
  const [audioLevels, setAudioLevels] = useState<Record<string, number>>({});
  const [catalogueGames, setCatalogueGames] = useState<GameInfo[]>([]);
  const [modelStatuses, setModelStatuses] = useState<GameModelStatus[]>([]);
  const [gameAddRequested, setGameAddRequested] = useState<GameAddRequestedMessage | null>(null);
  const controller = useSettings(client);

  useEffect(() => {
    // Mounted up front, often before the socket finishes connecting — a send() issued before
    // then is silently dropped, so request once immediately if already connected, and again on
    // every future connect/reconnect.
    if (client.state === 'connected') {
      client.send('ListGames');
    }
    return client.onStateChange((state) => {
      if (state === 'connected') {
        client.send('ListGames');
      }
    });
  }, [client]);

  useEffect(() => client.on('selectedGameExecutable', (content) => {
    const selected = content as Partial<SelectedGameExecutableMessage> | null;
    if (typeof selected?.requestId === 'string' && (typeof selected.filePath === 'string' || selected.filePath === null)) {
      setSelectedGameExecutable(selected as SelectedGameExecutableMessage);
    }
  }), [client]);

  useEffect(() => client.on('gameSearchResolved', (content) => {
    const response = content as Partial<ResolvedGameSearchMessage> | null;
    if (typeof response?.requestId === 'string') setResolvedGameSearch(response as ResolvedGameSearchMessage);
  }), [client]);

  useEffect(() => client.on('gameSearchResults', (content) => {
    const response = content as Partial<GameSearchResultsMessage> | null;
    if (typeof response?.requestId === 'string' && Array.isArray(response.results)) {
      setGameSearchResults(response as GameSearchResultsMessage);
    }
  }), [client]);

  useEffect(() => client.on('gameList', (content) => {
    if (Array.isArray(content)) setCatalogueGames(content as GameInfo[]);
  }), [client]);

  useEffect(() => client.on('modelStatus', (content) => {
    const message = content as Partial<ModelStatusMessage> | null;
    if (Array.isArray(message?.models)) setModelStatuses(message.models);
  }), [client]);

  useEffect(() => client.on('gameAddRequested', (content) => {
    const response = content as Partial<GameAddRequestedMessage> | null;
    if (typeof response?.requestId === 'string') setGameAddRequested(response as GameAddRequestedMessage);
  }), [client]);

  useEffect(() => client.on('audioLevels', (content) => {
    const message = content as Partial<AudioLevelsMessage> | null;
    if (!Array.isArray(message?.levels)) return;

    const next: Record<string, number> = {};
    for (const level of message.levels) {
      if (typeof level?.deviceId !== 'string' || typeof level.peak !== 'number') continue;
      next[level.deviceId] = Math.min(1, Math.max(0, Number.isFinite(level.peak) ? level.peak : 0));
    }
    setAudioLevels(next);
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
          {!controller.hasSettings ? (
            <p className="muted small settings-loading">
              Loading settings…
            </p>
          ) : (
          <>
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
          <AudioPage settings={controller.settings.audio} levels={audioLevels} update={controller.update} page={page} />
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
            focusGameId={focusGameId}
            onFocusHandled={onFocusGameHandled}
            selectedGameExecutable={selectedGameExecutable}
            settingsUpdateResult={controller.settingsUpdateResult}
            onBrowseExecutable={(requestId) => client.send('SelectGameExecutable', { requestId })}
            gameSearchResults={gameSearchResults}
            onSearchGames={(requestId, query) => client.send('SearchGames', { requestId, query, limit: 20 })}
            resolvedGameSearch={resolvedGameSearch}
            onResolveGameSearch={(requestId, input) => client.send('ResolveGameSearch', { requestId, input })}
            catalogueGames={catalogueGames}
            modelStatuses={modelStatuses}
            gameAddRequested={gameAddRequested}
            onRequestGame={(requestId, gameId) => client.send('RequestGameAdd', { requestId, gameId })}
            globalClipBeforeSeconds={controller.settings.recording.automaticClipBeforeSeconds}
            globalClipAfterSeconds={controller.settings.recording.automaticClipAfterSeconds}
            globalRecordingMode={controller.settings.recording.mode}
            automaticClipsEnabled={controller.settings.recording.automaticClipsEnabled === true}
          />
          )}
          {page === 'general' && (
          <GeneralPage
            client={client}
            settings={controller.settings.general}
            recording={controller.settings.recording}
            update={controller.update}
            page={page}
            appVersion={controller.appVersion}
          />
          )}
          {page === 'hotkeys' && (
          <HotkeysPage
            settings={controller.settings.hotkeys}
            update={controller.update}
            page={page}
            bufferDurationSeconds={controller.settings.buffer.duration}
          />
          )}
          </>
          )}
        </div>
      </div>
    </section>
  );
}
