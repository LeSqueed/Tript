// SPDX-License-Identifier: GPL-2.0-or-later

export type MessageHandler = (content: unknown) => void;

export interface Dispatcher {
  on(method: string, handler: MessageHandler): () => void;
  onAny(methods: readonly string[], handler: MessageHandler): () => void;
  dispatch(parsed: { method: string; content?: unknown }): void;
}

export function createDispatcher(_knownMethods: readonly string[] = []): Dispatcher {
  const handlers = new Map<string, Set<MessageHandler>>();

  function on(method: string, handler: MessageHandler): () => void {
    let set = handlers.get(method);
    if (!set) {
      set = new Set();
      handlers.set(method, set);
    }
    set.add(handler);
    return () => {
      set.delete(handler);
    };
  }

  function onAny(methods: readonly string[], handler: MessageHandler): () => void {
    const unsubscribers = methods.map((m) => on(m, handler));
    return () => {
      for (const unsub of unsubscribers) {
        unsub();
      }
    };
  }

  function dispatch(parsed: { method: string; content?: unknown }): void {
    const set = handlers.get(parsed.method);
    if (!set || set.size === 0) {
      return;
    }
    let firstError: unknown = null;
    for (const handler of [...set]) {
      try {
        handler(parsed.content);
      } catch (error) {
        if (firstError === null) {
          firstError = error;
        }
      }
    }
    if (firstError !== null) {
      throw firstError;
    }
  }

  return { on, onAny, dispatch };
}

export function isKnownMethod(method: string, knownMethods: readonly string[]): boolean {
  return knownMethods.includes(method);
}
