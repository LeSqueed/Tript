// SPDX-License-Identifier: GPL-2.0-or-later
//
// Message dispatch — the receiving half of the IPC client. The frontend narrows on `method`.

export type MessageHandler = (content: unknown) => void;

export interface Dispatcher {
  /** Register a handler for a method name. Returns an unsubscribe function. */
  on(method: string, handler: MessageHandler): () => void;
  /** Register a handler for several method names at once (aliases for one concern). */
  onAny(methods: readonly string[], handler: MessageHandler): () => void;
  /** Dispatch a parsed frame. Unknown methods are ignored. */
  dispatch(parsed: { method: string; content?: unknown }): void;
}

/**
 * Create a dispatcher. `knownMethods` is the canonical set this build understands — available to
 * callers for diagnostics (e.g. logging an unknown method without failing).
 */
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
      return; // unknown method — ignore, never error
    }
    // One handler throwing must not prevent the others from running: a broken UI listener must
    // not take down the IPC client's message loop. Rethrow the first failure after the loop.
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

/** Whether the given method name is one this build understands. */
export function isKnownMethod(method: string, knownMethods: readonly string[]): boolean {
  return knownMethods.includes(method);
}
