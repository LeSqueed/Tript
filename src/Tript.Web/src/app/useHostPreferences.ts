// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { GameInfo, RecordingState } from '../ipc/protocol';
import type { ConnectionState, IpcClient } from '../ipc/websocketClient';
import { useIpcMessage } from './useConnection';

const FALLBACK_BUILT_IN_GAME_IDS = ['57ZZVAZ0PJK8VQGPKB728QE57C'] as const;

export interface HostPreferences {
  builtInGameIds: readonly string[];
  convertHdrClipsToSdr: boolean;
  deleteLinkedHighlightsByDefault: boolean;
  recording: boolean;
}

interface SettingsMessage {
  settings?: {
    general?: { convertHdrClipsToSdr?: boolean };
    recording?: { deleteLinkedHighlightsByDefault?: boolean };
  };
}

export function useHostPreferences(client: IpcClient, connectionState: ConnectionState): HostPreferences {
  const [builtInGameIds, setBuiltInGameIds] = useState<readonly string[]>(FALLBACK_BUILT_IN_GAME_IDS);
  const [convertHdrClipsToSdr, setConvertHdrClipsToSdr] = useState(false);
  const [deleteLinkedHighlightsByDefault, setDeleteLinkedHighlightsByDefault] = useState(false);
  const [recording, setRecording] = useState(false);

  useEffect(() => {
    const remove = client.on('gameList', (content) => {
      const list = Array.isArray(content) ? (content as GameInfo[]) : [];
      const builtIn = list
        .filter((game) => game.builtIn === true && typeof game.id === 'string')
        .map((game) => String(game.id));
      if (builtIn.length > 0) {
        setBuiltInGameIds(builtIn);
      }
    });
    if (connectionState === 'connected') {
      client.send('ListGames');
    }
    return remove;
  }, [client, connectionState]);

  useIpcMessage(client, 'settings', (content) => {
    const settings = (content as SettingsMessage).settings;
    setConvertHdrClipsToSdr(settings?.general?.convertHdrClipsToSdr === true);
    setDeleteLinkedHighlightsByDefault(settings?.recording?.deleteLinkedHighlightsByDefault === true);
  });

  useIpcMessage(client, 'state', (content) => {
    const state = (content as { state?: RecordingState }).state;
    if (state)
      setRecording(state.recording === true);
  });

  return { builtInGameIds, convertHdrClipsToSdr, deleteLinkedHighlightsByDefault, recording };
}
