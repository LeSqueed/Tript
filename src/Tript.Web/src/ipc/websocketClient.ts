// SPDX-License-Identifier: GPL-2.0-or-later

import { serializeCommand, parseMessage as parseIncoming } from './serialize';
import { createDispatcher, type Dispatcher } from './dispatch';
import { controlSocketUrl, PROTOCOL_VERSION } from './endpoints';
import type { CommandName, CommandParameters } from './protocol';

export interface SocketLike {
  readyState: number;
  onopen: ((this: WebSocket, ev: Event) => unknown) | null;
  onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null;
  onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null;
  onerror: ((this: WebSocket, ev: Event) => unknown) | null;
  send(data: string): void;
  close(): void;
}

export const SOCKET_OPEN = 1;

export type WebSocketFactory = (url: string) => SocketLike;

export type ConnectionState = 'connecting' | 'connected' | 'disconnected';

export interface IpcClient {
  readonly state: ConnectionState;
  on(method: string, handler: (content: unknown) => void): () => void;
  send(method: CommandName, parameters?: CommandParameters): void;
  connect(): void;
  close(): void;
  onStateChange(handler: (state: ConnectionState) => void): () => void;
}

export interface IpcClientOptions {
  createSocket?: WebSocketFactory;
  reconnectBaseDelayMs?: number;
  reconnectMaxDelayMs?: number;
  url?: string;
}

const DEFAULT_RECONNECT_BASE_MS = 500;
const DEFAULT_RECONNECT_MAX_MS = 30_000;

export function createIpcClient(options: IpcClientOptions = {}): IpcClient {
  const {
    createSocket = (url) => new WebSocket(url),
    reconnectBaseDelayMs = DEFAULT_RECONNECT_BASE_MS,
    reconnectMaxDelayMs = DEFAULT_RECONNECT_MAX_MS,
    url = controlSocketUrl(),
  } = options;

  const dispatcher: Dispatcher = createDispatcher();
  const stateHandlers = new Set<(state: ConnectionState) => void>();

  let socket: SocketLike | null = null;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  let reconnectAttempt = 0;
  let closedByUser = false;
  let currentState: ConnectionState = 'disconnected';

  function setState(next: ConnectionState): void {
    if (next === currentState) {
      return;
    }
    currentState = next;
    for (const handler of [...stateHandlers]) {
      handler(next);
    }
  }

  function scheduleReconnect(): void {
    if (closedByUser || reconnectTimer !== null) {
      return;
    }
    const delay = Math.min(
      reconnectBaseDelayMs * 2 ** reconnectAttempt,
      reconnectMaxDelayMs,
    );
    reconnectAttempt += 1;
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      open();
    }, delay);
  }

  function open(): void {
    if (closedByUser) {
      return;
    }
    setState('connecting');
    let ws: SocketLike;
    try {
      ws = createSocket(url);
    } catch {
      scheduleReconnect();
      return;
    }
    socket = ws;

    ws.onopen = () => {
      if (ws !== socket) {
        return;
      }
      reconnectAttempt = 0;
      setState('connected');
      ws.send(serializeCommand('NewConnection', { protocolVersion: PROTOCOL_VERSION }));
    };

    ws.onmessage = (event) => {
      if (ws !== socket) {
        return;
      }
      const raw = typeof event.data === 'string' ? event.data : null;
      if (raw === null) {
        return;
      }
      const parsed = parseIncoming(raw);
      if (parsed !== null) {
        dispatcher.dispatch(parsed);
      }
    };

    ws.onclose = () => {
      if (ws !== socket) {
        return;
      }
      socket = null;
      setState('disconnected');
      scheduleReconnect();
    };

    ws.onerror = () => {
    };
  }

  function send(method: CommandName, parameters?: CommandParameters): void {
    const ws = socket;
    if (!ws || ws.readyState !== SOCKET_OPEN) {
      return;
    }
    ws.send(serializeCommand(method, parameters));
  }

  function connect(): void {
    closedByUser = false;
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    open();
  }

  function close(): void {
    closedByUser = true;
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    if (socket) {
      socket.onclose = null;
      socket.close();
      socket = null;
    }
    setState('disconnected');
  }

  return {
    get state() {
      return currentState;
    },
    on: (method, handler) => dispatcher.on(method, handler),
    send,
    connect,
    close,
    onStateChange(handler) {
      stateHandlers.add(handler);
      return () => {
        stateHandlers.delete(handler);
      };
    },
  };
}
