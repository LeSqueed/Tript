// SPDX-License-Identifier: GPL-2.0-or-later
//
// DisplayFallbackBanner tests: the banner appears when a settings push carries a
// `displayFallbackWarning`, names the missing monitor and the one in use, and stays dismissed only
// for that monitor — a warning about a different one comes back. The IPC client is a fake that
// captures the `settings` handler, so the wire shape is exercised without a socket.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, act } from '@testing-library/react';
import { DisplayFallbackBanner } from './DisplayFallbackBanner';
import type { IpcClient } from '../ipc/websocketClient';

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

function banner(): HTMLElement | null {
  return screen.queryByTestId('display-fallback-banner');
}

describe('DisplayFallbackBanner', () => {
  afterEach(() => {
    cleanup();
  });

  it('renders nothing until a push carries a warning', () => {
    const { client, emitSettings } = fakeClient();
    render(<DisplayFallbackBanner client={client} />);
    expect(banner()).toBeNull();
    act(() => {
      emitSettings({ settings: {} });
    });
    expect(banner()).toBeNull();
  });

  it('names the missing monitor and the one being used instead', () => {
    const { client, emitSettings } = fakeClient();
    render(<DisplayFallbackBanner client={client} />);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    const text = banner()?.textContent ?? '';
    expect(text).toContain('DP-3');
    expect(text).toContain('DP-1');
  });

  it('stays dismissed while the same monitor is still missing', () => {
    const { client, emitSettings } = fakeClient();
    render(<DisplayFallbackBanner client={client} />);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss monitor warning' }));
    expect(banner()).toBeNull();
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    expect(banner()).toBeNull();
  });

  it('comes back for a different monitor after a dismissal', () => {
    const { client, emitSettings } = fakeClient();
    render(<DisplayFallbackBanner client={client} />);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss monitor warning' }));
    act(() => {
      emitSettings({
        settings: {},
        displayFallbackWarning: { ...WARNING, requestedId: 'monitor-other', requestedLabel: 'HDMI-2' },
      });
    });
    expect(banner()?.textContent).toContain('HDMI-2');
  });

  it('clears itself when a later push carries no warning', () => {
    const { client, emitSettings } = fakeClient();
    render(<DisplayFallbackBanner client={client} />);
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: WARNING });
    });
    act(() => {
      emitSettings({ settings: {}, displayFallbackWarning: null });
    });
    expect(banner()).toBeNull();
  });
});
