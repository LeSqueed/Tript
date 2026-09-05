// SPDX-License-Identifier: GPL-2.0-or-later

import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { ModelStatusMessage } from '../ipc/protocol';
import type { IpcClient } from '../ipc/websocketClient';
import { RecorderBar } from './RecorderBar';

function mockClient(): IpcClient & {
  sent: { method: string; parameters?: unknown }[];
  emit(method: string, content: unknown): void;
} {
  const handlers = new Map<string, Set<(content: unknown) => void>>();
  const sent: { method: string; parameters?: unknown }[] = [];
  return {
    sent,
    state: 'connected',
    connect: () => {},
    close: () => {},
    send: (method, parameters) => sent.push({ method, parameters }),
    on: (method, handler) => {
      const methodHandlers = handlers.get(method) ?? new Set();
      methodHandlers.add(handler);
      handlers.set(method, methodHandlers);
      return () => methodHandlers.delete(handler);
    },
    onStateChange: () => () => {},
    emit: (method, content) => handlers.get(method)?.forEach((handler) => handler(content)),
  };
}

const DISPLAYS = [
  { id: 'd1', name: 'DP-1', width: 2560, height: 1440, primary: true },
  { id: 'd2', name: 'HDMI-2', width: 1920, height: 1080, primary: false },
];

function emitSettings(
  client: ReturnType<typeof mockClient>,
  method: 'Auto' | 'Game' | 'Display',
  displays: unknown[] | null,
  recordingMode: 'Session' | 'SessionWithReplayBuffer' | 'ReplayBufferOnly' = 'SessionWithReplayBuffer',
): void {
  act(() => {
    client.emit('settings', {
      settings: {
        capture: { method, display: null, displayLabel: null },
        recording: { mode: recordingMode },
      },
      availableDisplays: displays,
    });
  });
}

afterEach(cleanup);

describe('RecorderBar', () => {
  it('shows the detected game and records that specific game', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => client.emit('state', {
      state: {
        recording: false,
        game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
      },
    }));

    const bar = within(screen.getByTestId('recorder-bar'));
    expect(bar.getByText('Detected: Counter-Strike 2')).toBeTruthy();
    fireEvent.click(bar.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({ method: 'StartRecording', parameters: { gameId: 'cs2' } });
  });

  it('keeps parameterless recording when no game is detected', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({ method: 'StartRecording', parameters: undefined });
  });

  it('shows determinate download progress for the represented game only', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    const snapshot = {
      models: [
        { gameId: 'other', stage: 'error', message: 'Wrong game' },
        { gameId: 'cs2', stage: 'downloading', completedBytes: 25, totalBytes: 100 },
      ],
    } satisfies ModelStatusMessage;
    act(() => {
      client.emit('modelStatus', snapshot);
      client.emit('state', {
        state: {
          recording: false,
          game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
        },
      });
    });

    const progress = screen.getByRole('progressbar', { name: 'Downloading game model: 25%' });
    expect(progress.getAttribute('aria-valuenow')).toBe('25');
    expect(progress.textContent).toContain('Model 25%');
    expect(screen.queryByText(/Wrong game/)).toBeNull();
  });

  it('uses indeterminate progress semantics while verifying', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => {
      client.emit('state', {
        state: { recording: true, game: { id: 'cs2', name: 'CS2', detected: true } },
      });
      client.emit('modelStatus', {
        models: [{ gameId: 'cs2', stage: 'verifying' }],
      } satisfies ModelStatusMessage);
    });

    const progress = screen.getByRole('progressbar', { name: 'Verifying model' });
    expect(progress.getAttribute('aria-valuenow')).toBeNull();
    expect(progress.textContent).toContain('Verifying model');
  });

  it('does not retain ready or statuses omitted from a replacement snapshot', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);

    act(() => {
      client.emit('state', {
        state: { recording: false, game: { id: 'cs2', name: 'CS2', detected: true } },
      });
      client.emit('modelStatus', {
        models: [{ gameId: 'cs2', stage: 'unsupported', message: 'No model available' }],
      } satisfies ModelStatusMessage);
    });
    expect(screen.getByRole('status').textContent).toBe('Model unsupported: No model available');

    act(() => client.emit('modelStatus', {
      models: [{ gameId: 'cs2', stage: 'ready', revision: 2 }],
    } satisfies ModelStatusMessage));
    expect(screen.queryByTestId('model-status')).toBeNull();

    act(() => client.emit('modelStatus', { models: [] } satisfies ModelStatusMessage));
    expect(screen.queryByTestId('model-status')).toBeNull();
  });

  it('disables record when the capture method is Game and no game is detected', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Game', DISPLAYS);
    const record = screen.getByRole('button', { name: 'Record' }) as HTMLButtonElement;
    expect(record.disabled).toBe(true);
    expect(screen.queryByTestId('capture-source')).toBeNull();
  });

  it('keeps record enabled when a game is detected under the Game method', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Game', DISPLAYS);
    act(() => client.emit('state', {
      state: { recording: false, game: { id: 'cs2', name: 'CS2', detected: true } },
    }));
    const record = screen.getByRole('button', { name: 'Record' }) as HTMLButtonElement;
    expect(record.disabled).toBe(false);
    expect(screen.queryByTestId('capture-source')).toBeNull();
  });

  it('offers a screen swap when no game is detected under Auto', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS);
    const record = screen.getByRole('button', { name: 'Record' }) as HTMLButtonElement;
    expect(record.disabled).toBe(false);
    expect(screen.getByTestId('capture-source')).toBeTruthy();
    expect(screen.getByRole('combobox', { name: 'Capture display' })).toBeTruthy();
  });

  it('records the chosen monitor as a one-off override', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS);
    const select = screen.getByRole('combobox', { name: 'Capture display' }) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: 'd2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: 'd2' },
    });
  });

  it('records the primary monitor as a one-off override', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Display', DISPLAYS);
    const select = screen.getByRole('combobox', { name: 'Capture display' }) as HTMLSelectElement;
    fireEvent.change(select, { target: { value: '__primary_display__' } });
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: null },
    });
  });

  it('labels the idle manual action from the configured global buffer-only mode', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS, 'ReplayBufferOnly');

    fireEvent.click(screen.getByRole('button', { name: 'Start buffer' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: null },
    });
  });

  it('labels the detected-game action from its buffer-only override', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    act(() => {
      client.emit('settings', {
        settings: {
          game: {
            gameCaptureTimeout: 10,
            gameList: [{
              id: 'cs2',
              name: 'Counter-Strike 2',
              recordingModeOverride: { mode: 'ReplayBufferOnly' },


            }],
          },
        },
      });
      client.emit('state', {
        state: {
          recording: false,
          game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
        },
      });
    });

    expect(screen.getByRole('button', { name: 'Start buffer' })).toBeTruthy();
  });

  it('renders active buffer-only state with its activity context and stop action', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" nowSeconds={1_700_000_065} />);

    act(() => client.emit('state', {
      state: {
        recording: true,
        activeRecordingMode: 'ReplayBufferOnly',
        startedAt: 1_700_000_000,
        game: { id: 'cs2', name: 'Counter-Strike 2', detected: true },
      },
    }));

    const bar = within(screen.getByTestId('recorder-bar'));
    expect(bar.getByText('Buffering')).toBeTruthy();
    expect(bar.getByTestId('recording-elapsed').textContent).toBe('1:05');
    expect(bar.getByText('Counter-Strike 2')).toBeTruthy();
    fireEvent.click(bar.getByRole('button', { name: 'Stop' }));
    expect(client.sent.at(-1)).toEqual({ method: 'StopRecording', parameters: undefined });
  });

  it('keeps a one-off monitor choice across unrelated settings pushes', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS);
    fireEvent.change(screen.getByRole('combobox', { name: 'Capture display' }), {
      target: { value: 'd2' },
    });

    emitSettings(client, 'Auto', DISPLAYS);
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));

    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: 'd2' },
    });
  });

  it('falls back from a one-off monitor that disappears before recording starts', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS);
    fireEvent.change(screen.getByRole('combobox', { name: 'Capture display' }), {
      target: { value: 'd2' },
    });

    emitSettings(client, 'Auto', [DISPLAYS[0]]);

    const select = screen.getByRole('combobox', { name: 'Capture display' }) as HTMLSelectElement;
    expect(select.value).toBe('__primary_display__');
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: null },
    });
  });

  it('clears the one-off monitor choice when a detected game hides the selector', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Auto', DISPLAYS);
    fireEvent.change(screen.getByRole('combobox', { name: 'Capture display' }), {
      target: { value: 'd2' },
    });

    act(() => client.emit('state', {
      state: { recording: false, game: { id: 'cs2', name: 'CS2', detected: true } },
    }));
    expect(screen.queryByTestId('capture-source')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { gameId: 'cs2' },
    });

    act(() => client.emit('state', { state: { recording: false, game: null } }));
    expect((screen.getByRole('combobox', { name: 'Capture display' }) as HTMLSelectElement).value)
      .toBe('__primary_display__');
  });

  it('returns to the saved monitor after consuming a one-off override', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" />);
    emitSettings(client, 'Display', DISPLAYS);
    fireEvent.change(screen.getByRole('combobox', { name: 'Capture display' }), {
      target: { value: 'd2' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Record' }));

    fireEvent.click(screen.getByRole('button', { name: 'Record' }));
    expect(client.sent.at(-1)).toEqual({
      method: 'StartRecording',
      parameters: { applyDisplay: true, displayId: null },
    });
  });

  it('loads an available model during a recording', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" trainingFeatureEnabled />);
    act(() => {
      client.emit('state', {
        state: { recording: true, activeModelGameId: 'overwatch' },
      });
      client.emit('availableRecordingModels', {
        models: [
          { gameId: 'Overwatch', name: 'Overwatch' },
          { gameId: 'Apex', name: 'Apex Legends' },
        ],
      });
    });

    const select = screen.getByRole('combobox', { name: 'Detection model' }) as HTMLSelectElement;
    expect(select.value).toBe('Overwatch');
    expect((select.options[0] as HTMLOptionElement).disabled).toBe(true);
    fireEvent.change(select, { target: { value: 'Apex' } });
    expect(client.sent.at(-1)).toEqual({
      method: 'ActivateRecordingModel',
      parameters: { gameId: 'Apex' },
    });
  });

  it('counts the clips being created in the top bar', () => {
    const client = mockClient();
    const { rerender } = render(
      <RecorderBar client={client} connectionState="connected" clipJobCount={1} />,
    );
    expect(screen.getByTestId('clip-creation-status').textContent).toBe('Creating clips…');

    rerender(<RecorderBar client={client} connectionState="connected" clipJobCount={2} />);
    expect(screen.getByTestId('clip-creation-status').textContent).toBe('Creating 2 clips…');

    rerender(<RecorderBar client={client} connectionState="connected" clipJobCount={1} />);
    expect(screen.getByTestId('clip-creation-status').textContent).toBe('Creating clips…');

    rerender(<RecorderBar client={client} connectionState="connected" clipJobCount={0} />);
    expect(screen.queryByTestId('clip-creation-status')).toBeNull();
  });

  it('shows the disabled model placeholder when no model is active', () => {
    const client = mockClient();
    render(<RecorderBar client={client} connectionState="connected" trainingFeatureEnabled />);
    act(() => {
      client.emit('state', { state: { recording: true, activeModelGameId: null } });
      client.emit('availableRecordingModels', {
        models: [{ gameId: 'Overwatch', name: 'Overwatch' }],
      });
    });

    const select = screen.getByRole('combobox', { name: 'Detection model' }) as HTMLSelectElement;
    expect(select.value).toBe('');
    expect(select.selectedOptions[0]?.textContent).toBe('Load model…');
    expect(select.selectedOptions[0]?.disabled).toBe(true);
  });
});
