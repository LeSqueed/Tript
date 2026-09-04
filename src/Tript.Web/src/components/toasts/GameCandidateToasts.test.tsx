// SPDX-License-Identifier: GPL-2.0-or-later
//
// GameCandidateToasts tests: the suggestion appears only for a well-formed gameCandidate push,
// clears on gameCandidateCleared, both actions hand the backend the executable path, a correlated
// failure is reported under the message, and a success takes the toast down.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { GameCandidateToasts } from './GameCandidateToasts';
import { ToastProvider } from '../ui/toast/ToastProvider';
import type { IpcClient } from '../../ipc/websocketClient';

function fakeClient(): {
  client: IpcClient;
  emit: (method: string, content: unknown) => void;
  sent: { method: string; parameters?: unknown }[];
} {
  const handlers = new Map<string, (content: unknown) => void>();
  const sent: { method: string; parameters?: unknown }[] = [];
  const client: IpcClient = {
    state: 'connecting',
    on(method, handler) {
      handlers.set(method, handler);
      return () => handlers.delete(method);
    },
    send(method, parameters) {
      sent.push({ method, parameters });
    },
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => {}),
  };
  return { client, emit: (method, content) => handlers.get(method)?.(content), sent };
}

const CANDIDATE = {
  pid: 4001,
  executable: 'firefox',
  executablePath: 'C:\\Program Files\\Firefox\\firefox.exe',
};

function renderBridge(client: IpcClient): void {
  render(
    <ToastProvider>
      <GameCandidateToasts client={client} />
    </ToastProvider>,
  );
}

describe('GameCandidateToasts', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('shows a suggestion for a well-formed candidate push', () => {
    const { client, emit } = fakeClient();
    renderBridge(client);

    act(() => emit('gameCandidate', CANDIDATE));

    expect(screen.getByRole('status').textContent).toContain(CANDIDATE.executablePath);
  });

  it('does not show malformed candidate content', () => {
    const { client, emit } = fakeClient();
    renderBridge(client);

    act(() => {
      emit('gameCandidate', {});
      emit('gameCandidate', { executable: 'x' });
      emit('gameCandidate', null);
    });

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('clears when the backend reports the candidate left fullscreen', () => {
    const { client, emit } = fakeClient();
    renderBridge(client);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => emit('gameCandidateCleared', { executablePath: CANDIDATE.executablePath }));
    act(() => vi.advanceTimersByTime(200));

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('keeps the full-path confirmation visible until correlated add success', () => {
    const { client, emit, sent } = fakeClient();
    renderBridge(client);
    act(() => emit('gameCandidate', CANDIDATE));

    fireEvent.click(screen.getByRole('button', { name: 'Add as custom game' }));

    const requestId = (sent.at(-1)?.parameters as { requestId: string }).requestId;
    expect(sent).toContainEqual({
      method: 'AddGameCandidate',
      parameters: { requestId, name: CANDIDATE.executable, executablePath: CANDIDATE.executablePath },
    });
    expect(screen.getByRole('status').textContent).toContain(CANDIDATE.executablePath);
    // The action is in flight, so neither button may fire twice.
    expect((screen.getByRole('button', { name: 'Add as custom game' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Ignore' }) as HTMLButtonElement).disabled).toBe(true);

    act(() => emit('gameCandidateActionResult', {
      requestId,
      executablePath: CANDIDATE.executablePath,
      action: 'add',
      success: true,
    }));
    act(() => vi.advanceTimersByTime(200));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('sends IgnoreGameCandidate when the user dismisses it', () => {
    const { client, emit, sent } = fakeClient();
    renderBridge(client);
    act(() => emit('gameCandidate', CANDIDATE));

    fireEvent.click(screen.getByRole('button', { name: 'Ignore' }));

    const requestId = (sent.at(-1)?.parameters as { requestId: string }).requestId;
    expect(sent).toContainEqual({
      method: 'IgnoreGameCandidate',
      parameters: { requestId, executablePath: CANDIDATE.executablePath },
    });
    expect(screen.getByRole('status')).toBeTruthy();
  });

  it('retains the candidate and reports a correlated action failure under the message', () => {
    const { client, emit, sent } = fakeClient();
    renderBridge(client);
    act(() => emit('gameCandidate', CANDIDATE));
    fireEvent.click(screen.getByRole('button', { name: 'Ignore' }));
    const requestId = (sent.at(-1)?.parameters as { requestId: string }).requestId;

    act(() => emit('gameCandidateActionResult', {
      requestId,
      executablePath: CANDIDATE.executablePath,
      action: 'ignore',
      success: false,
      error: 'Could not persist the ignored executable.',
    }));

    expect(screen.getByRole('status').textContent).toContain(CANDIDATE.executablePath);
    expect(screen.getByRole('alert').textContent).toBe('Could not persist the ignored executable.');
    // The failure does not take the suggestion down: the actions are offered again.
    expect((screen.getByRole('button', { name: 'Add as custom game' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('keeps a different candidate visible when another executable\'s clear arrives', () => {
    const { client, emit } = fakeClient();
    renderBridge(client);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => emit('gameCandidateCleared', { executablePath: 'C:\\Other\\thing.exe' }));

    expect(screen.getByRole('status').textContent).toContain(CANDIDATE.executablePath);
  });
});
