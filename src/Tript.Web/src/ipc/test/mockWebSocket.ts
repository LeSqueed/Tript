// SPDX-License-Identifier: GPL-2.0-or-later
//
// A minimal controllable WebSocket for exercising the IPC client without a real socket. Matches
// the DOM WebSocket shape the client touches: constructor, readyState, OPEN, send, close, and the
// event handlers onopen/onmessage/onclose/onerror. This is a test double, not a browser polyfill.

export const CONNECTING = 0;
export const OPEN = 1;
export const CLOSING = 2;
export const CLOSED = 3;

export type MockMessageEvent = { data: unknown };

export class MockWebSocket {
  static CONNECTING = CONNECTING;
  static OPEN = OPEN;
  static CLOSING = CLOSING;
  static CLOSED = CLOSED;

  static instances: MockWebSocket[] = [];

  url: string;
  readyState = CONNECTING;
  sent: string[] = [];

  onopen: (() => void) | null = null;
  onmessage: ((event: MockMessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;

  /** Whether the factory should throw on construction, simulating a refused connection. */
  static throwOnNextConstruct = false;

  constructor(url: string) {
    if (MockWebSocket.throwOnNextConstruct) {
      MockWebSocket.throwOnNextConstruct = false;
      throw new Error('connection refused');
    }
    this.url = url;
    MockWebSocket.instances.push(this);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    if (this.readyState === CLOSED) {
      return;
    }
    this.readyState = CLOSED;
    if (this.onclose) {
      this.onclose();
    }
  }

  /** Test helper: open the socket, firing onopen. */
  serverOpen(): void {
    this.readyState = OPEN;
    if (this.onopen) {
      this.onopen();
    }
  }

  /** Test helper: push a frame from the server. */
  serverMessage(data: string): void {
    if (this.onmessage) {
      this.onmessage({ data });
    }
  }

  /** Test helper: drop the connection, firing onclose. */
  serverClose(): void {
    this.readyState = CLOSED;
    if (this.onclose) {
      this.onclose();
    }
  }

  static reset(): void {
    MockWebSocket.instances = [];
    MockWebSocket.throwOnNextConstruct = false;
  }
}

export function createMockSocketFactory(): { factory: (url: string) => MockWebSocket } {
  const factory = (url: string) => new MockWebSocket(url);
  return { factory };
}
