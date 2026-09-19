// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { StorageToasts } from './StorageToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { IpcClient } from '../../ipc/websocketClient';
import type { StorageStatusMessage } from '../../ipc/protocol';

const GIGABYTE = 1024 * 1024 * 1024;

function fakeClient(): { client: IpcClient; emit: (content: unknown) => void } {
  const handlers = new Map<string, (content: unknown) => void>();
  const client: IpcClient = {
    state: 'connecting',
    on(method, handler) {
      handlers.set(method, handler);
      return () => {
        handlers.delete(method);
      };
    },
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  return {
    client,
    emit: (content) => handlers.get('storageStatus')?.(content),
  };
}

function status(patch: Partial<StorageStatusMessage> = {}): StorageStatusMessage {
  return {
    pressure: 'ok',
    freeBytes: 400 * GIGABYTE,
    totalBytes: 500 * GIGABYTE,
    minimumFreeBytes: 20 * GIGABYTE,
    warnFreeBytes: 60 * GIGABYTE,
    recordingBlocked: false,
    policyConfirmed: true,
    whenFull: 'PauseRecording',
    keepSharingWhenFull: true,
    volumeRoot: 'T:\\',
    root: 'T:\\Tript',
    scratchFreeBytes: 0,
    scratchLow: false,
    ...patch,
  };
}

function renderFeed(client: IpcClient, onOpen = vi.fn()) {
  render(
    <ToastProvider>
      <StorageToasts client={client} onOpenStorageSettings={onOpen} />
    </ToastProvider>,
  );
  return onOpen;
}

describe('StorageToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('says nothing while there is room', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status());
    });

    expect(screen.queryByTestId('storage-warning-banner')).toBeNull();
    expect(screen.queryByTestId('storage-policy-banner')).toBeNull();
    expect(screen.queryByTestId('storage-critical-banner')).toBeNull();
  });

  it('asks the user to choose a policy before the limit is reached', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'warning', freeBytes: 50 * GIGABYTE, policyConfirmed: false }));
    });

    expect(screen.getByTestId('storage-policy-banner')).toBeTruthy();
    expect(screen.queryByTestId('storage-warning-banner')).toBeNull();
  });

  it('warns without nagging once the policy has been chosen', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'warning', freeBytes: 50 * GIGABYTE, policyConfirmed: true }));
    });

    expect(screen.queryByTestId('storage-policy-banner')).toBeNull();
    expect(screen.getByTestId('storage-warning-banner').textContent).toContain('pause recording');
  });

  it('says what the reclaim policy will do instead', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'warning', freeBytes: 50 * GIGABYTE, whenFull: 'ReclaimOldest' }));
    });

    expect(screen.getByTestId('storage-warning-banner').textContent).toContain('oldest');
  });

  it('reports the hold when recording is blocked', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'critical', freeBytes: 2 * GIGABYTE, recordingBlocked: true }));
    });

    const banner = screen.getByTestId('storage-critical-banner');
    expect(banner.textContent).toContain('Recording is on hold');
    expect(banner.textContent).toContain('T:\\');
  });

  it('replaces the warning with the critical banner rather than stacking them', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'warning', freeBytes: 50 * GIGABYTE, policyConfirmed: false }));
    });
    act(() => {
      emit(status({ pressure: 'critical', freeBytes: 2 * GIGABYTE, recordingBlocked: true }));
    });
    act(() => vi.advanceTimersByTime(400));

    expect(screen.queryByTestId('storage-policy-banner')).toBeNull();
    expect(screen.getByTestId('storage-critical-banner')).toBeTruthy();
  });

  it('clears everything once there is room again', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit(status({ pressure: 'critical', freeBytes: 2 * GIGABYTE, recordingBlocked: true }));
    });
    act(() => {
      emit(status());
    });
    act(() => vi.advanceTimersByTime(400));

    expect(screen.queryByTestId('storage-critical-banner')).toBeNull();
  });

  it('opens the storage settings from the banner', () => {
    const { client, emit } = fakeClient();
    const onOpen = renderFeed(client);
    act(() => {
      emit(status({ pressure: 'warning', freeBytes: 50 * GIGABYTE, policyConfirmed: false }));
    });

    fireEvent.click(screen.getByRole('button', { name: 'Choose now' }));
    expect(onOpen).toHaveBeenCalled();
  });

  it('ignores a payload it cannot read', () => {
    const { client, emit } = fakeClient();
    renderFeed(client);
    act(() => {
      emit({ pressure: 'from-the-future' });
    });

    expect(screen.queryByTestId('storage-critical-banner')).toBeNull();
    expect(screen.queryByTestId('storage-warning-banner')).toBeNull();
  });
});
