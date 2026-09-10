// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { ConnectionToasts } from './ConnectionToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { HostReachability } from '../../ipc/hostProbe';

function renderWith(reachability: HostReachability | null) {
  return render(
    <ToastProvider>
      <ConnectionToasts reachability={reachability} />
    </ToastProvider>,
  );
}

describe('ConnectionToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('says the key is stale when the host rejects it', () => {
    renderWith('rejected');
    const toast = screen.getByTestId('connection-banner');
    expect(toast.textContent).toMatch(/restarted/i);
    expect(toast.getAttribute('role')).toBe('alert');
  });

  it('says the host is not answering when it is unreachable', () => {
    renderWith('unreachable');
    const toast = screen.getByTestId('connection-banner');
    expect(toast.textContent).toMatch(/not answering/i);
    expect(toast.getAttribute('role')).toBe('alert');
  });

  it('says only that the control socket is down when the host still accepts the key', () => {
    renderWith('accepted');
    const toast = screen.getByTestId('connection-banner');
    expect(toast.textContent).toMatch(/control socket/i);
    expect(toast.getAttribute('role')).toBe('status');
  });

  it('renders nothing while the host is fine', () => {
    renderWith(null);
    expect(screen.queryByTestId('connection-banner')).toBeNull();
  });

  it('stays up as long as the problem does, on its own', () => {
    renderWith('rejected');
    act(() => vi.advanceTimersByTime(120_000));
    expect(screen.getByTestId('connection-banner')).toBeTruthy();
  });

  it('comes down when the reachability resolves', () => {
    const { rerender } = renderWith('rejected');
    expect(screen.getByTestId('connection-banner')).toBeTruthy();

    rerender(
      <ToastProvider>
        <ConnectionToasts reachability={null} />
      </ToastProvider>,
    );
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByTestId('connection-banner')).toBeNull();
  });
});
