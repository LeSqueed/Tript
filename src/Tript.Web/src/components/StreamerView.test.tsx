// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { createIpcClient } from '../ipc/websocketClient';
import { MockWebSocket, createMockSocketFactory } from '../ipc/test/mockWebSocket';
import type { StreamerStatusMessage } from '../ipc/protocol';
import { readStreamerStatus, StreamerView } from './StreamerView';

function activeSocket(): MockWebSocket {
  const sockets = MockWebSocket.instances;
  return sockets[sockets.length - 1];
}

function renderStreamer(streaming = { shareEnabled: false, shareWhen: 'WhileObsRuns', senderName: 'Tript' }) {
  const { factory } = createMockSocketFactory();
  const client = createIpcClient({ createSocket: factory });
  render(<StreamerView client={client} />);
  client.connect();
  const ws = activeSocket();
  act(() => {
    ws.serverOpen();
    ws.serverMessage(JSON.stringify({ method: 'settings', content: { settings: { streaming } } }));
  });
  return ws;
}

function pushStatus(ws: MockWebSocket, status: Partial<StreamerStatusMessage>) {
  act(() => {
    ws.serverMessage(JSON.stringify({
      method: 'streamerStatus',
      content: {
        state: 'off',
        shareEnabled: false,
        obsRunning: false,
        senderName: 'Tript',
        width: 0,
        height: 0,
        hookConflictSuspected: false,
        ...status,
      },
    }));
  });
}

function sentFrames(ws: MockWebSocket): { method: string; parameters?: { settings?: Record<string, unknown> } }[] {
  return ws.sent.map((frame) => JSON.parse(frame));
}

beforeEach(() => {
  MockWebSocket.reset();
});

afterEach(() => {
  cleanup();
});

describe('StreamerView', () => {
  it('asks for the sharing status when it connects', () => {
    const ws = renderStreamer();
    expect(sentFrames(ws).map((frame) => frame.method)).toContain('GetStreamerStatus');
  });

  it('turns sharing on through the streaming settings', () => {
    const ws = renderStreamer();
    fireEvent.click(screen.getByLabelText('Share the game picture with OBS'));
    const updates = sentFrames(ws).filter((frame) => frame.method === 'UpdateSettings');
    expect(updates.at(-1)?.parameters?.settings).toEqual({ streaming: { shareEnabled: true } });
  });

  it('offers the share timing only once sharing is on', () => {
    renderStreamer();
    expect(screen.queryByRole('radiogroup', { name: 'When to share' })).toBeNull();
    cleanup();
    MockWebSocket.reset();

    const ws = renderStreamer({ shareEnabled: true, shareWhen: 'WhileObsRuns', senderName: 'Tript' });
    fireEvent.click(screen.getByRole('radio', { name: 'Always' }));
    const updates = sentFrames(ws).filter((frame) => frame.method === 'UpdateSettings');
    expect(updates.at(-1)?.parameters?.settings).toEqual({ streaming: { shareWhen: 'Always' } });
  });

  it('shows the live picture size and the OBS version', () => {
    const ws = renderStreamer({ shareEnabled: true, shareWhen: 'WhileObsRuns', senderName: 'Tript' });
    pushStatus(ws, {
      state: 'live',
      shareEnabled: true,
      obsRunning: true,
      obsVersion: '32.0.1',
      width: 2560,
      height: 1440,
      adapterName: 'Test GPU',
    });

    const status = screen.getByTestId('streamer-status');
    expect(status.textContent).toContain('Sharing with OBS');
    expect(status.textContent).toContain('2560 x 1440');
    expect(status.textContent).toContain('Open, version 32.0.1');
    expect(status.textContent).toContain('Test GPU');
  });

  it('opens the Spout2 plugin page in a browser rather than inside Tript', () => {
    const ws = renderStreamer();
    fireEvent.click(screen.getByRole('button', { name: 'Spout2 Plugin' }));
    const opened = sentFrames(ws).filter((frame) => frame.method === 'OpenInBrowser');
    expect(opened).toHaveLength(1);
    expect((opened[0].parameters as unknown as { url: string }).url)
      .toBe('https://github.com/Off-World-Live/obs-spout2-plugin');
  });

  it('warns about a likely capture conflict until sharing is live', () => {
    const ws = renderStreamer();
    pushStatus(ws, { state: 'off', obsRunning: true, hookConflictSuspected: true });
    expect(screen.getByTestId('streamer-conflict')).toBeTruthy();

    pushStatus(ws, { state: 'live', obsRunning: true, hookConflictSuspected: true, width: 1, height: 1 });
    expect(screen.queryByTestId('streamer-conflict')).toBeNull();
  });

  it('keeps a sender name OBS cannot list from being saved', () => {
    const ws = renderStreamer();
    const input = screen.getByLabelText('Sender name') as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'Tript é' } });
    fireEvent.blur(input);
    expect(input.value).toBe('Tript');

    fireEvent.change(input, { target: { value: 'Tript Game' } });
    fireEvent.blur(input);
    const updates = sentFrames(ws).filter((frame) => frame.method === 'UpdateSettings');
    expect(updates).toHaveLength(1);
    expect(updates[0].parameters?.settings).toEqual({ streaming: { senderName: 'Tript Game' } });
  });
});

describe('readStreamerStatus', () => {
  it('rejects an unknown state', () => {
    expect(readStreamerStatus({ state: 'streaming' })).toBeNull();
    expect(readStreamerStatus(null)).toBeNull();
  });

  it('fills fields the backend left out', () => {
    expect(readStreamerStatus({ state: 'waitingForObs' })).toEqual({
      state: 'waitingForObs',
      shareEnabled: false,
      obsRunning: false,
      obsVersion: undefined,
      senderName: '',
      width: 0,
      height: 0,
      adapterName: undefined,
      hookConflictSuspected: false,
    });
  });
});
