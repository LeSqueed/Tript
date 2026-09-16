// SPDX-License-Identifier: GPL-2.0-or-later

import { useEffect, useRef, useState } from 'react';
import {
  createIpcClient,
  type ConnectionState,
  type IpcClient,
  type IpcClientOptions,
} from '../ipc/websocketClient';
import type { CommandName, CommandParameters, MessageName } from '../ipc/protocol';

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

export function useSendOnConnect(client: IpcClient, method: CommandName): void {
  useEffect(() => {
    if (client.state === 'connected') {
      client.send(method);
    }
    return client.onStateChange((state) => {
      if (state === 'connected') {
        client.send(method);
      }
    });
  }, [client, method]);
}

export function useIpcMessage(
  client: IpcClient,
  method: MessageName,
  handler: (content: unknown) => void,
): void {
  const handlerRef = useRef(handler);
  useEffect(() => {
    handlerRef.current = handler;
  });
  useEffect(() => client.on(method, (content) => handlerRef.current(content)), [client, method]);
}
