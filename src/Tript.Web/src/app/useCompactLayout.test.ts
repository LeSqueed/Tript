// SPDX-License-Identifier: GPL-2.0-or-later

import { renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { COMPACT_QUERY, useCompactLayout } from './useCompactLayout';

const realMatchMedia = window.matchMedia;
afterEach(() => {
  window.matchMedia = realMatchMedia;
});

function stubMatchMedia(matches: boolean) {
  const listeners = new Set<(event: MediaQueryListEvent) => void>();
  const list = {
    matches,
    media: COMPACT_QUERY,
    onchange: null,
    addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.add(listener);
    },
    removeEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => {
      listeners.delete(listener);
    },
    addListener: () => {},
    removeListener: () => {},
    dispatchEvent: () => false,
  };
  window.matchMedia = vi.fn(() => list) as unknown as typeof window.matchMedia;
  return { list, listeners };
}

describe('useCompactLayout', () => {
  it('reports the query result on first render', () => {
    stubMatchMedia(true);
    expect(renderHook(() => useCompactLayout()).result.current).toBe(true);
  });

  it('reports not-compact at desktop width', () => {
    stubMatchMedia(false);
    expect(renderHook(() => useCompactLayout()).result.current).toBe(false);
  });

  it('unsubscribes on unmount', () => {
    const { listeners } = stubMatchMedia(false);
    const { unmount } = renderHook(() => useCompactLayout());
    expect(listeners.size).toBe(1);
    unmount();
    expect(listeners.size).toBe(0);
  });
});
