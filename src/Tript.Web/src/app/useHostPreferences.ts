// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { RecordingState } from '../ipc/protocol';
import type { ConnectionState, IpcClient } from '../ipc/websocketClient';
import { useIpcMessage } from './useConnection';

export interface HostPreferences {
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
  const [convertHdrClipsToSdr, setConvertHdrClipsToSdr] = useState(false);
  const [deleteLinkedHighlightsByDefault, setDeleteLinkedHighlightsByDefault] = useState(false);
  const [recording, setRecording] = useState(false);

  useEffect(() => {
    if (connectionState === 'connected') {
      client.send('ListGames');
    }
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

  return { convertHdrClipsToSdr, deleteLinkedHighlightsByDefault, recording };
}
