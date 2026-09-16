// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it, vi } from 'vitest';
import { createDispatcher } from './dispatch';

describe('createDispatcher', () => {
  it('routes a message to the handler for its method', () => {
    const dispatcher = createDispatcher();
    const handler = vi.fn();
    dispatcher.on('state', handler);

    dispatcher.dispatch({ method: 'state', content: { recording: true } });

    expect(handler).toHaveBeenCalledWith({ recording: true });
  });

  it('does not call handlers registered for other methods', () => {
    const dispatcher = createDispatcher();
    const handler = vi.fn();
    dispatcher.on('settings', handler);

    dispatcher.dispatch({ method: 'state' });

    expect(handler).not.toHaveBeenCalled();
  });

  it('ignores unknown methods rather than erroring', () => {
    const dispatcher = createDispatcher(['settings', 'state']);
    expect(() => dispatcher.dispatch({ method: 'futureMethod', content: {} })).not.toThrow();
  });

  it('preserves the cause on a settings message for the UI', () => {
    const dispatcher = createDispatcher();
    let seen: unknown;
    dispatcher.on('settings', (content) => {
      seen = content;
    });

    dispatcher.dispatch({
      method: 'settings',
      content: { settings: { recordingMode: 'manual' }, cause: 'updateSettings' },
    });

    expect(seen).toMatchObject({
      settings: { recordingMode: 'manual' },
      cause: 'updateSettings',
    });
  });

  it('preserves the cause on a state message', () => {
    const dispatcher = createDispatcher();
    let seen: unknown;
    dispatcher.on('state', (content) => {
      seen = content;
    });

    dispatcher.dispatch({
      method: 'state',
      content: { state: { recording: true }, cause: 'startRecording' },
    });

    expect(seen).toMatchObject({ cause: 'startRecording' });
  });

  it('calls multiple handlers for the same method', () => {
    const dispatcher = createDispatcher();
    const a = vi.fn();
    const b = vi.fn();
    dispatcher.on('state', a);
    dispatcher.on('state', b);

    dispatcher.dispatch({ method: 'state', content: {} });

    expect(a).toHaveBeenCalledOnce();
    expect(b).toHaveBeenCalledOnce();
  });

  it('returns an unsubscribe function that stops delivery', () => {
    const dispatcher = createDispatcher();
    const handler = vi.fn();
    const unsubscribe = dispatcher.on('state', handler);
    unsubscribe();

    dispatcher.dispatch({ method: 'state', content: {} });

    expect(handler).not.toHaveBeenCalled();
  });

  it('onAny registers the same handler for several methods and unsubscribes them all', () => {
    const dispatcher = createDispatcher();
    const handler = vi.fn();
    const unsubscribe = dispatcher.onAny(['settings', 'state'], handler);

    dispatcher.dispatch({ method: 'settings', content: {} });
    dispatcher.dispatch({ method: 'state', content: {} });
    expect(handler).toHaveBeenCalledTimes(2);

    unsubscribe();
    dispatcher.dispatch({ method: 'settings', content: {} });
    expect(handler).toHaveBeenCalledTimes(2);
  });

  it('handlers that throw do not stop other handlers or corrupt the dispatcher', () => {
    const dispatcher = createDispatcher();
    const bad = vi.fn(() => {
      throw new Error('boom');
    });
    const good = vi.fn();
    dispatcher.on('state', bad);
    dispatcher.on('state', good);

    expect(() => dispatcher.dispatch({ method: 'state', content: {} })).toThrow();
    expect(good).toHaveBeenCalledOnce();
    expect(() => dispatcher.dispatch({ method: 'state', content: {} })).toThrow();
  });
});
