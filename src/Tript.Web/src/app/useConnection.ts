// SPDX-License-Identifier: GPL-2.0-or-later
//
// A hook that owns an IpcClient instance for the lifetime of a component tree.

import { useEffect, useRef, useState } from 'react';
import {
  createIpcClient,
  type ConnectionState,
  type IpcClient,
  type IpcClientOptions,
} from '../ipc/websocketClient';
import type { CommandName, CommandParameters } from '../ipc/protocol';

export function useIpcClient(options: IpcClientOptions = {}): {
  client: IpcClient;
  connectionState: ConnectionState;
} {
  const clientRef = useRef<IpcClient | null>(null);
  if (clientRef.current === null) {
    clientRef.current = createIpcClient(options);
  }
  const [connectionState, setConnectionState] = useState<ConnectionState>(
    clientRef.current.state,
  );

  useEffect(() => {
    const client = clientRef.current!;
    const unsubscribe = client.onStateChange(setConnectionState);
    client.connect();
    return () => {
      unsubscribe();
      client.close();
    };
  }, []);

  return { client: clientRef.current, connectionState };
}

export type { ConnectionState };

export function useCommand(client: IpcClient) {
  return (method: CommandName, parameters?: CommandParameters) => {
    client.send(method, parameters);
  };
}
