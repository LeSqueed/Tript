// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IpcClient } from '../../ipc/websocketClient';
import { TrainingPublishForm } from './TrainingPublishForm';

afterEach(cleanup);

function createClient() {
  return {
    on: vi.fn(() => () => undefined),
    send: vi.fn(),
  } as unknown as IpcClient & { send: ReturnType<typeof vi.fn> };
}

function fillCredentials() {
  fireEvent.change(screen.getByLabelText('Admin password'), { target: { value: 'secret' } });
}

describe('TrainingPublishForm minimum app version', () => {
  it('publishes without a minimum version when the field is left empty', () => {
    const client = createClient();
    render(<TrainingPublishForm client={client} gameId="game-1" hasModel />);
    fillCredentials();

    fireEvent.click(screen.getByRole('button', { name: 'Publish model' }));

    const [, parameters] = client.send.mock.calls.find(([method]) => method === 'PublishTrainingModel')!;
    expect(parameters).not.toHaveProperty('minimumAppVersion');
  });

  it('sends the minimum version it was given', () => {
    const client = createClient();
    render(<TrainingPublishForm client={client} gameId="game-1" hasModel />);
    fillCredentials();
    fireEvent.change(screen.getByLabelText(/Minimum app version/), { target: { value: ' 1.2.1 ' } });

    fireEvent.click(screen.getByRole('button', { name: 'Publish model' }));

    const [, parameters] = client.send.mock.calls.find(([method]) => method === 'PublishTrainingModel')!;
    expect(parameters).toMatchObject({ minimumAppVersion: '1.2.1' });
  });

  it('blocks publishing while the minimum version is malformed', () => {
    const client = createClient();
    render(<TrainingPublishForm client={client} gameId="game-1" hasModel />);
    fillCredentials();
    fireEvent.change(screen.getByLabelText(/Minimum app version/), { target: { value: '1.2' } });

    expect((screen.getByRole('button', { name: 'Publish model' }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText('Use the form 1.2.1.')).toBeTruthy();
    fireEvent.keyDown(screen.getByLabelText(/Minimum app version/), { key: 'Enter' });
    expect(client.send).not.toHaveBeenCalled();
  });
});
