// SPDX-License-Identifier: GPL-2.0-or-later
//
// GameCandidateBanner tests: the suggestion appears only for a well-formed gameCandidate push,
// clears on gameCandidateCleared, and both actions hand the backend the executable path.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { GameCandidateBanner } from './GameCandidateBanner';
import type { IpcClient } from '../ipc/websocketClient';

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

describe('GameCandidateBanner', () => {
  afterEach(() => cleanup());

  it('shows a suggestion for a well-formed candidate push', () => {
    const { client, emit } = fakeClient();
    render(<GameCandidateBanner client={client} />);

    act(() => emit('gameCandidate', CANDIDATE));

    expect(screen.getByRole('status').textContent).toContain('firefox');
  });

  it('does not show malformed candidate content', () => {
    const { client, emit } = fakeClient();
    render(<GameCandidateBanner client={client} />);

    act(() => {
      emit('gameCandidate', {});
      emit('gameCandidate', { executable: 'x' });
      emit('gameCandidate', null);
    });

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('clears when the backend reports the candidate left fullscreen', () => {
    const { client, emit } = fakeClient();
    render(<GameCandidateBanner client={client} />);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => emit('gameCandidateCleared', { executablePath: CANDIDATE.executablePath }));

    expect(screen.queryByRole('status')).toBeNull();
  });

  it('keeps the full-path confirmation visible until correlated add success', () => {
    const { client, emit, sent } = fakeClient();
    render(<GameCandidateBanner client={client} />);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => screen.getByRole('button', { name: 'Add as custom game' }).click());

    const requestId = (sent.at(-1)?.parameters as { requestId: string }).requestId;
    expect(sent).toContainEqual({
      method: 'AddGameCandidate',
      parameters: { requestId, name: CANDIDATE.executable, executablePath: CANDIDATE.executablePath },
    });
    expect(screen.getByRole('status').textContent).toContain(CANDIDATE.executablePath);

    act(() => emit('gameCandidateActionResult', {
      requestId,
      executablePath: CANDIDATE.executablePath,
      action: 'add',
      success: true,
    }));
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('sends IgnoreGameCandidate when the user dismisses it', () => {
    const { client, emit, sent } = fakeClient();
    render(<GameCandidateBanner client={client} />);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => screen.getByRole('button', { name: 'Ignore' }).click());

    const requestId = (sent.at(-1)?.parameters as { requestId: string }).requestId;
    expect(sent).toContainEqual({
      method: 'IgnoreGameCandidate',
      parameters: { requestId, executablePath: CANDIDATE.executablePath },
    });
    expect(screen.getByRole('status')).toBeTruthy();
  });

  it('retains the candidate and reports a correlated action failure', () => {
    const { client, emit, sent } = fakeClient();
    render(<GameCandidateBanner client={client} />);
    act(() => emit('gameCandidate', CANDIDATE));
    act(() => screen.getByRole('button', { name: 'Ignore' }).click());
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
  });

  it('keeps a different candidate visible when another executables clear arrives', () => {
    const { client, emit } = fakeClient();
    render(<GameCandidateBanner client={client} />);
    act(() => emit('gameCandidate', CANDIDATE));

    act(() => emit('gameCandidateCleared', { executablePath: 'C:\\Other\\thing.exe' }));

    expect(screen.getByRole('status').textContent).toContain('firefox');
  });
});
