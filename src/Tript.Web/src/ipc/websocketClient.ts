// SPDX-License-Identifier: GPL-2.0-or-later
//
// The IPC client: a typed WebSocket client over the control socket.
//
// Responsibilities:
//   - Connect to the control socket (ws://localhost:44030/).
//   - Send commands in the envelope shape (serializeCommand).
//   - Receive backend → frontend messages and dispatch them by method (createDispatcher).
//   - Reconnect on close/error, with backoff.
//   - On socket open, send NewConnection (with the protocol version) — this triggers a full state
//     push from the backend, so the UI always converges to a consistent view.
//
// The WebSocket implementation is injectable so the reconnection and envelope logic can be unit
// tested against a mock rather than a real socket.

import { serializeCommand, parseMessage as parseIncoming } from './serialize';
import { createDispatcher, type Dispatcher } from './dispatch';
import { CONTROL_SOCKET_URL, PROTOCOL_VERSION } from './endpoints';
import type { CommandName, CommandParameters } from './protocol';

/**
 * The surface of a WebSocket the IPC client touches. Uses the DOM event handler signatures so the
 * real `WebSocket` is structurally compatible; tests substitute a mock whose no-argument handlers
 * are assignable to these (a function with fewer parameters is assignable to one with more).
 */
export interface SocketLike {
  readyState: number;
  onopen: ((this: WebSocket, ev: Event) => unknown) | null;
  onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null;
  onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null;
  onerror: ((this: WebSocket, ev: Event) => unknown) | null;
  send(data: string): void;
  close(): void;
}

/** The WebSocket ready-state constant this client waits for before sending. */
export const SOCKET_OPEN = 1;

export type WebSocketFactory = (url: string) => SocketLike;

export type ConnectionState = 'connecting' | 'connected' | 'disconnected';

export interface IpcClient {
  /** The current connection state. */
  readonly state: ConnectionState;
  /** Register a handler for a backend → frontend message method. */
  on(method: string, handler: (content: unknown) => void): () => void;
  /** Send a command. `parameters` is optional; commands with no args send no `parameters` at all. */
  send(method: CommandName, parameters?: CommandParameters): void;
  /** Manually open (or reopen) the connection. */
  connect(): void;
  /** Close the connection and stop reconnecting. */
  close(): void;
  /** Subscribe to connection-state changes. Returns an unsubscribe function. */
  onStateChange(handler: (state: ConnectionState) => void): () => void;
}

export interface IpcClientOptions {
  /** WebSocket factory. Defaults to the global WebSocket (the real one). */
  createSocket?: WebSocketFactory;
  /** Backoff between reconnection attempts, in ms. Default 500, doubling to a 30s cap. */
  reconnectBaseDelayMs?: number;
  reconnectMaxDelayMs?: number;
  /** The socket URL to connect to. Defaults to the control socket. */
  url?: string;
}

const DEFAULT_RECONNECT_BASE_MS = 500;
const DEFAULT_RECONNECT_MAX_MS = 30_000;

export function createIpcClient(options: IpcClientOptions = {}): IpcClient {
  const {
    createSocket = (url) => new WebSocket(url),
    reconnectBaseDelayMs = DEFAULT_RECONNECT_BASE_MS,
    reconnectMaxDelayMs = DEFAULT_RECONNECT_MAX_MS,
    url = CONTROL_SOCKET_URL,
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
        return; // a newer socket replaced us
      }
      reconnectAttempt = 0;
      setState('connected');
      // NewConnection on socket open triggers a full state push from the backend.
      ws.send(serializeCommand('NewConnection', { protocolVersion: PROTOCOL_VERSION }));
    };

    ws.onmessage = (event) => {
      if (ws !== socket) {
        return;
      }
      const raw = typeof event.data === 'string' ? event.data : null;
      if (raw === null) {
        return; // binary frames are not part of the contract
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
      // The close event follows; error alone is not terminal.
    };
  }

  function send(method: CommandName, parameters?: CommandParameters): void {
    const ws = socket;
    if (!ws || ws.readyState !== SOCKET_OPEN) {
      // Not connected — drop rather than buffer. The UI reflects connection state and the
      // NewConnection push re-syncs everything on open.
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
