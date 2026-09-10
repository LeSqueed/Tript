// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DisplayFallbackToasts } from './DisplayFallbackToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { IpcClient } from '../../ipc/websocketClient';

function fakeClient(): { client: IpcClient; emitSettings: (content: unknown) => void } {
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
    emitSettings: (content) => {
      const handler = handlers.get('settings');
      if (handler) {
        handler(content);
      }
    },
  };
}

const WARNING = {
  requestedId: 'monitor-gone',
  requestedLabel: 'DP-3',
  usingId: 'monitor-1',
  usingLabel: 'DP-1',
};

function renderBridge(client: IpcClient): void {
  render(
    <ToastProvider>
      <DisplayFallbackToasts client={client} />
    </ToastProvider>,
  );
}

function toast(): HTMLElement | null {
  return screen.queryByTestId('display-fallback-banner');
}

describe('DisplayFallbackToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('renders nothing until a push carries a warning', () => {
    const { client, emitSettings } = fakeClient();
    renderBridge(client);
    expect(toast()).toBeNull();
    act(() => {
      emitSettings({ settings: {} });
    });
    expect(toast()).toBeNull();
  });

  it('names the missing monitor and the one being used instead', () => {
    const { client, emitSettings } = fakeClient();
    renderBridge(client);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    const text = toast()?.textContent ?? '';
    expect(text).toContain('DP-3');
    expect(text).toContain('DP-1');
  });

  it('stays dismissed while the same monitor is still missing', () => {
    const { client, emitSettings } = fakeClient();
    renderBridge(client);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => vi.advanceTimersByTime(200));
    expect(toast()).toBeNull();
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    expect(toast()).toBeNull();
  });

  it('comes back for a different monitor after a dismissal', () => {
    const { client, emitSettings } = fakeClient();
    renderBridge(client);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }));
    act(() => vi.advanceTimersByTime(200));
    act(() => {
      emitSettings({
        settings: {},
        displayFallbackWarning: { ...WARNING, requestedId: 'monitor-other', requestedLabel: 'HDMI-2' },
      });
    });
    expect(toast()?.textContent).toContain('HDMI-2');
  });

  it('clears itself when a later push carries no warning', () => {
    const { client, emitSettings } = fakeClient();
    renderBridge(client);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: null });
    });
    act(() => vi.advanceTimersByTime(200));
    expect(toast()).toBeNull();
  });
});
