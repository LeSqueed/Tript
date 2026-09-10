// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import {
  MAX_READING_MS,
  MAX_VISIBLE,
  MIN_READING_MS,
  dismissItem,
  dismissKey,
  expireToast,
  pushToast,
  readingDuration,
  releaseToast,
  removeToast,
  type ToastItem,
  type ToastSpec,
} from './toastModel';

let nextId = 1;

function push(items: ToastItem[], spec: Partial<ToastSpec>): ToastItem[] {
  return pushToast(items, { kind: 'info', message: 'a message', duration: 0, ...spec }, nextId++);
}

function stackOf(count: number): ToastItem[] {
  let items: ToastItem[] = [];
  for (let i = 0; i < count; i += 1) {
    items = push(items, { message: `toast ${i + 1}` });
  }
  return items;
}

describe('readingDuration', () => {
  it('reads at 60 ms per character of visible text', () => {
    expect(readingDuration(undefined, 'x'.repeat(100), undefined)).toBe(6000);
  });

  it('counts the title and the note as well, spaces between the parts included', () => {
    const withParts = readingDuration('t'.repeat(50), 'm'.repeat(50), 'n'.repeat(50));
    expect(withParts).toBe((50 + 1 + 50 + 1 + 50) * 60);
  });

  it('never reads shorter than the minimum, so a short note is still readable', () => {
    expect(readingDuration(undefined, 'hi', undefined)).toBe(MIN_READING_MS);
  });

  it('never reads longer than the maximum, so a long one does not sit up forever', () => {
    expect(readingDuration(undefined, 'x'.repeat(1000), undefined)).toBe(MAX_READING_MS);
  });
});

describe('pushToast', () => {
  it('appends a new toast', () => {
    const items = push([], { message: 'first' });
    expect(items).toHaveLength(1);
    expect(items[0].state).toBe('visible');
  });

  it('derives the clock from reading time when no duration is given', () => {
    const items = pushToast([], { kind: 'info', message: 'x'.repeat(100) }, nextId++);
    expect(items[0].duration).toBe(6000);
  });

  it('keeps an explicit duration, and treats 0 as permanent', () => {
    const explicit = pushToast([], { kind: 'info', message: 'x'.repeat(100), duration: 2500 }, nextId++);
    expect(explicit[0].duration).toBe(2500);
    const permanent = pushToast([], { kind: 'info', message: 'x'.repeat(100), duration: 0 }, nextId++);
    expect(permanent[0].duration).toBe(0);
  });

  it('caps the stack and queues what does not fit', () => {
    const items = stackOf(MAX_VISIBLE + 2);
    expect(items.filter((toast) => toast.state === 'visible')).toHaveLength(MAX_VISIBLE);
    expect(items.filter((toast) => toast.state === 'waiting')).toHaveLength(2);
    expect(items[MAX_VISIBLE].message).toBe(`toast ${MAX_VISIBLE + 1}`);
  });

  it('replaces a keyed toast in place: same position, new content, fresh clock state', () => {
    const items = push(push([], { key: 'a', message: 'first' }), { key: 'b', message: 'second' });
    const replaced = push(items, { key: 'a', message: 'replaced' });
    expect(replaced).toHaveLength(2);
    expect(replaced[0].message).toBe('replaced');
    expect(replaced[0].id).not.toBe(items[0].id);
    expect(replaced[1].message).toBe('second');
    expect(replaced[0].held).toBe(false);
  });

  it('revives a keyed toast that is finishing its exit rather than stacking a second copy', () => {
    const items = push([], { key: 'a', message: 'first' });
    const leaving = dismissKey(items, 'a');
    expect(leaving[0].state).toBe('leaving');
    const revived = push(leaving, { key: 'a', message: 'back' });
    expect(revived).toHaveLength(1);
    expect(revived[0].state).toBe('visible');
    expect(revived[0].message).toBe('back');
  });

  it('keeps a replaced queued toast waiting when the stack is full', () => {
    let items = stackOf(MAX_VISIBLE);
    items = push(items, { key: 'queued', message: 'waiting' });
    const replaced = push(items, { key: 'queued', message: 'still waiting' });
    expect(replaced.filter((toast) => toast.state === 'visible')).toHaveLength(MAX_VISIBLE);
    expect(replaced.find((toast) => toast.key === 'queued')?.state).toBe('waiting');
  });
});

describe('dismissal', () => {
  it('takes a visible toast into its exit', () => {
    const items = push([], { message: 'bye' });
    const leaving = dismissItem(items, items[0].id);
    expect(leaving[0].state).toBe('leaving');
  });

  it('drops a queued toast without it ever rendering', () => {
    let items = stackOf(MAX_VISIBLE);
    const id = nextId++;
    items = pushToast(items, { kind: 'info', message: 'overflow', duration: 0 }, id);
    expect(items.filter((toast) => toast.state === 'waiting')).toHaveLength(1);
    const dropped = dismissItem(items, id);
    expect(dropped).toHaveLength(MAX_VISIBLE);
  });

  it('dismisses by key', () => {
    const items = push([], { key: 'k', message: 'bye' });
    const leaving = dismissKey(items, 'k');
    expect(leaving[0].state).toBe('leaving');
    expect(dismissKey(items, 'other')).toBe(items);
  });
});

describe('the timer and the pointer', () => {
  it('expires a visible toast when its time is up', () => {
    const items = pushToast([], { kind: 'info', message: 'x'.repeat(100) }, nextId++);
    const expired = expireToast(items, items[0].id, false);
    expect(expired[0].state).toBe('leaving');
  });

  it('holds an expiring toast while the pointer is over it', () => {
    const items = pushToast([], { kind: 'info', message: 'x'.repeat(100) }, nextId++);
    const held = expireToast(items, items[0].id, true);
    expect(held[0].state).toBe('visible');
    expect(held[0].held).toBe(true);
  });

  it('releases a held toast into its exit once the pointer is gone', () => {
    const items = pushToast([], { kind: 'info', message: 'x'.repeat(100) }, nextId++);
    const held = expireToast(items, items[0].id, true);
    expect(held[0].state).toBe('visible');
    expect(held[0].held).toBe(true);
    const released = releaseToast(held, held[0].id);
    expect(released[0].state).toBe('leaving');
  });

  it('ignores a release for a toast whose time never ran out', () => {
    const items = pushToast([], { kind: 'info', message: 'x'.repeat(100) }, nextId++);
    expect(releaseToast(items, items[0].id)).toEqual(items);
  });
});

describe('removal', () => {
  it('removes the finished toast and promotes the queue into its slot', () => {
    const before = stackOf(MAX_VISIBLE + 1);
    const items = removeToast(before, before[0].id);
    expect(items).toHaveLength(MAX_VISIBLE);
    expect(items.filter((toast) => toast.state === 'visible')).toHaveLength(MAX_VISIBLE);
    expect(items.filter((toast) => toast.state === 'waiting')).toHaveLength(0);
    expect(items[0].message).toBe('toast 2');
  });
});
