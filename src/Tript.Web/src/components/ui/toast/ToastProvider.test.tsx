// SPDX-License-Identifier: GPL-2.0-or-later
//
// The toast provider: the stack's clocks and the pointer contract. Fake timers throughout, the
// same convention as the rest of the suite.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ToastProvider, useToast, type ToastApi } from './ToastProvider';
import type { ToastSpec } from './toastModel';

function renderProvider(): { api: ToastApi; push: (spec: ToastSpec) => number } {
  const sink: { api?: ToastApi } = {};
  function ApiSink() {
    sink.api = useToast();
    return null;
  }
  render(<ToastProvider><ApiSink /></ToastProvider>);
  const api = sink.api as ToastApi;
  return {
    api,
    push: (spec: ToastSpec) => {
      let id = 0;
      act(() => {
        id = api.push(spec);
      });
      return id;
    },
  };
}

describe('ToastProvider', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('renders no stack until something is pushed', () => {
    renderProvider();
    expect(screen.queryByRole('status')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('shows a pushed toast', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'Saved.', duration: 0 });
    expect(screen.getByRole('status').textContent).toContain('Saved.');
  });

  it('reads a timed toast at 60 ms per character, then takes it down on its own', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'x'.repeat(100) }); // 6000 ms
    const toast = screen.getByRole('status');

    act(() => vi.advanceTimersByTime(5999));
    expect(screen.getByRole('status')).toBe(toast);

    act(() => vi.advanceTimersByTime(1)); // the time is up: the exit plays
    expect(screen.getByRole('status')).toBe(toast);

    act(() => vi.advanceTimersByTime(200)); // the exit is done: it is gone
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('keeps a permanent toast until it is dismissed', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'Still here.', duration: 0 });

    act(() => vi.advanceTimersByTime(120_000));
    expect(screen.getByRole('status').textContent).toContain('Still here.');

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('fires the toast onDismiss when the user closes it, and not when it times out', () => {
    const onDismiss = vi.fn();
    const { push } = renderProvider();
    push({ kind: 'info', message: 'Mine.', duration: 0, onDismiss });

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => vi.advanceTimersByTime(200));
    expect(onDismiss).toHaveBeenCalledTimes(1);

    push({ kind: 'info', message: 'Timed.', onDismiss });
    act(() => vi.advanceTimersByTime(20_000));
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it('holds a timed toast while the pointer is over it, and lets it go once the pointer leaves', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'Hover me.', duration: 2000 });
    const toast = screen.getByRole('status');

    fireEvent.pointerEnter(toast);
    act(() => vi.advanceTimersByTime(2000)); // the time is up, but the pointer is over it
    expect(screen.getByRole('status')).toBe(toast);

    fireEvent.pointerLeave(toast);
    act(() => vi.advanceTimersByTime(399)); // the grace has not run
    expect(screen.getByRole('status')).toBe(toast);

    act(() => vi.advanceTimersByTime(1)); // the grace is up
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('lets the pointer move back within the grace', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'Come back.', duration: 2000 });
    const toast = screen.getByRole('status');

    fireEvent.pointerEnter(toast);
    act(() => vi.advanceTimersByTime(2000));
    fireEvent.pointerLeave(toast);
    act(() => vi.advanceTimersByTime(100));
    fireEvent.pointerEnter(toast); // back on time

    act(() => vi.advanceTimersByTime(4000));
    expect(screen.getByRole('status')).toBe(toast);

    fireEvent.pointerLeave(toast);
    act(() => vi.advanceTimersByTime(400));
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('keeps four toasts up and queues the fifth until a slot opens', () => {
    const { api, push } = renderProvider();
    const first = push({ kind: 'info', message: 'one', duration: 0 });
    push({ kind: 'info', message: 'two', duration: 0 });
    push({ kind: 'info', message: 'three', duration: 0 });
    push({ kind: 'info', message: 'four', duration: 0 });
    push({ kind: 'info', message: 'five', duration: 0 });

    expect(screen.queryByText('five')).toBeNull();
    expect(screen.getByText('one')).toBeTruthy();

    act(() => {
      api.dismissSelf(first);
    });
    act(() => vi.advanceTimersByTime(200));
    expect(screen.getByText('five')).toBeTruthy();
  });

  it('replaces a keyed toast in place instead of stacking a second copy', () => {
    const { push } = renderProvider();
    push({ key: 'a', kind: 'info', message: 'first', duration: 0 });
    push({ key: 'a', kind: 'info', message: 'second', duration: 0 });

    expect(screen.queryByText('first')).toBeNull();
    expect(screen.getByText('second')).toBeTruthy();
    expect(screen.getAllByRole('status')).toHaveLength(1);
  });

  it('restarts the clock when a replacement changes the message', () => {
    const { push } = renderProvider();
    push({ key: 'a', kind: 'info', message: 'x'.repeat(100) }); // 6000 ms
    act(() => vi.advanceTimersByTime(5000));
    push({ key: 'a', kind: 'info', message: 'short' }); // clamped to 4000 ms, fires at t = 9000

    // Past the old clock, plus its exit: a clock that was not re-armed would be gone by now.
    act(() => vi.advanceTimersByTime(1001 + 200));
    expect(screen.getByText('short')).toBeTruthy();

    act(() => vi.advanceTimersByTime(9000 - 6201)); // t = 9000: the new clock is up
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('revives a keyed toast that is mid-exit when the backend pushes it again', () => {
    const { api, push } = renderProvider();
    push({ key: 'a', kind: 'info', message: 'bye', duration: 0 });
    act(() => {
      api.dismiss('a');
    });
    // The exit has played but not finished.
    expect(screen.getByText('bye')).toBeTruthy();

    push({ key: 'a', kind: 'info', message: 'back', duration: 0 });
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByText('bye')).toBeNull();
    expect(screen.getByText('back')).toBeTruthy();
    expect(screen.getAllByRole('status')).toHaveLength(1);
  });

  it('draws the countdown ring on timed toasts only', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'permanent', duration: 0 });
    expect(screen.getByRole('status').querySelector('.toast-timer')).toBeNull();

    push({ kind: 'info', message: 'timed' });
    const toasts = screen.getAllByRole('status');
    expect(toasts.map((toast) => toast.querySelector('.toast-timer') !== null)).toEqual([false, true]);
  });

  it('renders actions and runs their callbacks', () => {
    const onClick = vi.fn();
    const { push } = renderProvider();
    push({
      kind: 'info',
      message: 'Do something?',
      duration: 0,
      actions: [{ label: 'Do it', onClick }],
    });

    fireEvent.click(screen.getByRole('button', { name: 'Do it' }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('marks errors as alerts and everything else as statuses', () => {
    const { push } = renderProvider();
    push({ kind: 'error', message: 'It failed.', duration: 0 });
    expect(screen.getByRole('alert').textContent).toContain('It failed.');
    push({ kind: 'warning', message: 'Careful.', duration: 0 });
    expect(screen.getByRole('status').textContent).toContain('Careful.');
  });

  it('shows a note under the message', () => {
    const { push } = renderProvider();
    push({ kind: 'info', message: 'The action failed.', note: 'Could not persist it.', duration: 0 });
    expect(screen.getByRole('alert').textContent).toBe('Could not persist it.');
  });
});
