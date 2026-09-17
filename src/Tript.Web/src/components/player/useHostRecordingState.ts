// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useState } from 'react';
import type { RecordingState } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';

export function useHostRecordingState(client: IpcClient, filePath: string | undefined): {
  automaticClips: RecordingState['automaticClips'];
  recording: boolean;
} {
  const [automaticClips, setAutomaticClips] = useState<RecordingState['automaticClips']>(null);
  const [recording, setRecording] = useState(false);

  useEffect(() => client.on('state', (content) => {
    const state = (content as { state?: RecordingState }).state;
    const job = state?.automaticClips;
    setRecording(state?.recording === true);
    setAutomaticClips(job?.sourceSessionPath === filePath ? job : null);
  }), [client, filePath]);

  return { automaticClips, recording };
}
