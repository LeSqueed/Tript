// SPDX-License-Identifier: GPL-2.0-or-later

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

  serverOpen(): void {
    this.readyState = OPEN;
    if (this.onopen) {
      this.onopen();
    }
  }

  serverMessage(data: string): void {
    if (this.onmessage) {
      this.onmessage({ data });
    }
  }

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
