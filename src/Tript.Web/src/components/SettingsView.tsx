// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState, type ComponentProps } from 'react';
import type { IpcClient } from '../ipc/websocketClient';
import type { GameAddRequestedMessage, GameInfo, GameModelStatus, GameSearchResultsMessage, ModelStatusMessage, ResolvedGameSearchMessage, SelectedGameExecutableMessage } from '../ipc/protocol';
import { useIpcMessage, useSendOnConnect } from '../app/useConnection';
import { useAudioLevels } from '../settings/useAudioLevels';
import { useSettings, type SettingsPageName } from '../settings/useSettings';
import { RecordingPage } from '../settings/pages/RecordingPage';
import { HighlightsPage } from '../settings/pages/HighlightsPage';
import { AudioPage } from '../settings/pages/AudioPage';
import { CapturePage } from '../settings/pages/CapturePage';
import { GamePage } from '../settings/pages/GamePage';
import { GeneralPage } from '../settings/pages/GeneralPage';
import { HotkeysPage } from '../settings/pages/HotkeysPage';
import { StoragePage } from '../settings/pages/StoragePage';
import { useStorage } from '../components/storage/useStorage';
import { PlatformCapabilitiesContext, usePlatformCapabilities } from '../app/platformCapabilities';

const PAGES: { id: SettingsPageName; label: string }[] = [
  { id: 'general', label: 'General' },
  { id: 'recording', label: 'Recording' },
  { id: 'buffer', label: 'Highlights' },
  { id: 'storage', label: 'Storage' },
  { id: 'audio', label: 'Audio' },
  { id: 'capture', label: 'Capture' },
  { id: 'game', label: 'Games' },
  { id: 'hotkeys', label: 'Hotkeys' },
];

function LiveAudioPage({
  client,
  visible,
  ...page
}: Omit<ComponentProps<typeof AudioPage>, 'levels'> & { client: IpcClient; visible: boolean }) {
  const levels = useAudioLevels(client, visible);
  return <AudioPage {...page} levels={levels} />;
}

export function SettingsView({
  client,
  builtInGameIds = [],
  focusGameId = null,
  onFocusGameHandled,
  focusPage = null,
  onFocusPageHandled,
  active = true,
}: {
  client: IpcClient;
  builtInGameIds?: readonly string[];
  focusGameId?: string | null;
  onFocusGameHandled?: () => void;
  focusPage?: SettingsPageName | null;
  onFocusPageHandled?: () => void;
  active?: boolean;
}) {
  const [page, setPage] = useState<SettingsPageName>('general');

  useEffect(() => {
    if (focusGameId) setPage('game');
  }, [focusGameId]);

  useEffect(() => {
    if (!focusPage) return;
    setPage(focusPage);
    onFocusPageHandled?.();
  }, [focusPage]);
  const [selectedGameExecutable, setSelectedGameExecutable] = useState<SelectedGameExecutableMessage | null>(null);
  const [gameSearchResults, setGameSearchResults] = useState<GameSearchResultsMessage | null>(null);
  const [resolvedGameSearch, setResolvedGameSearch] = useState<ResolvedGameSearchMessage | null>(null);
  const [catalogueGames, setCatalogueGames] = useState<GameInfo[]>([]);
  const [modelStatuses, setModelStatuses] = useState<GameModelStatus[]>([]);
  const [gameAddRequested, setGameAddRequested] = useState<GameAddRequestedMessage | null>(null);
  const controller = useSettings(client);
  const inheritedCapabilities = usePlatformCapabilities();
  const capabilities = controller.platformCapabilities ?? inheritedCapabilities;
  const storage = useStorage(client);

  useSendOnConnect(client, 'ListGames');

  useIpcMessage(client, 'selectedGameExecutable', (content) => {
    const selected = content as Partial<SelectedGameExecutableMessage> | null;
    if (typeof selected?.requestId === 'string' && (typeof selected.filePath === 'string' || selected.filePath === null)) {
      setSelectedGameExecutable(selected as SelectedGameExecutableMessage);
    }
  });

  useIpcMessage(client, 'gameSearchResolved', (content) => {
    const response = content as Partial<ResolvedGameSearchMessage> | null;
    if (typeof response?.requestId === 'string') setResolvedGameSearch(response as ResolvedGameSearchMessage);
  });

  useIpcMessage(client, 'gameSearchResults', (content) => {
    const response = content as Partial<GameSearchResultsMessage> | null;
    if (typeof response?.requestId === 'string' && Array.isArray(response.results)) {
      setGameSearchResults(response as GameSearchResultsMessage);
    }
  });

  useIpcMessage(client, 'gameList', (content) => {
    if (Array.isArray(content)) setCatalogueGames(content as GameInfo[]);
  });

  useIpcMessage(client, 'modelStatus', (content) => {
    const message = content as Partial<ModelStatusMessage> | null;
    if (Array.isArray(message?.models)) setModelStatuses(message.models);
  });

  useIpcMessage(client, 'gameAddRequested', (content) => {
    const response = content as Partial<GameAddRequestedMessage> | null;
    if (typeof response?.requestId === 'string') setGameAddRequested(response as GameAddRequestedMessage);
  });

  return (
    <PlatformCapabilitiesContext.Provider value={capabilities}>
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
          />
          )}
          {page === 'storage' && (
          <StoragePage
            settings={controller.settings.storage}
            recording={controller.settings.recording}
            update={controller.update}
            page={page}
            externalPushCount={controller.externalPushCount}
            status={storage.status}
            report={storage.report}
            onReclaim={storage.reclaim}
            onRefresh={storage.refresh}
            onClearTrash={() => client.send('PurgeTrash')}
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
          <LiveAudioPage
            client={client}
            visible={active}
            settings={controller.settings.audio}
            update={controller.update}
            page={page}
          />
          )}
          {page === 'capture' && (
          <CapturePage
            settings={controller.settings.capture}
            game={controller.settings.game}
            update={controller.update}
            page={page}
            availableDisplays={controller.availableDisplays}
            externalPushCount={controller.externalPushCount}
            onForgetScreenChoice={() => client.send('ForgetScreenShareChoice')}
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
            onConfigureDesktopShortcuts={() => client.send('ConfigureGlobalHotkeys')}
          />
          )}
          </>
          )}
        </div>
      </div>
    </section>
    </PlatformCapabilitiesContext.Provider>
  );
}
