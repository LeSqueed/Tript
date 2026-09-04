import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { TrainingView } from './TrainingView';
import type { IpcClient } from '../ipc/websocketClient';
import type { TrainingMessage, TrainingSample } from '../ipc/protocol';

function createClient() {
  const handlers = new Map<string, Set<(content: unknown) => void>>();
  const client = {
    state: 'connected' as const,
    on: (method: string, handler: (content: unknown) => void) => {
      const listeners = handlers.get(method) ?? new Set();
      listeners.add(handler);
      handlers.set(method, listeners);
      return () => listeners.delete(handler);
    },
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => undefined),
  } satisfies IpcClient;
  return {
    client,
    emit(method: string, content: unknown) {
      for (const handler of handlers.get(method) ?? []) handler(content);
    },
  };
}

function sample(index: number): TrainingSample {
  return {
    id: `sample-${index}`,
    imageFile: `sample-${index}.png`,
    sourcePath: `C:/capture-${index}.mp4`,
    timestampSeconds: index,
    imageWidth: 2000,
    imageHeight: 1125,
    labels: [],
  };
}

describe('TrainingView sample gallery', () => {
  afterEach(cleanup);

  it('correlates later-page previews and preserves the full image ratio when opening a sample', async () => {
    const { client, emit } = createClient();
    const training: TrainingMessage = {
      gameId: 'game-1',
      revision: '1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: Array.from({ length: 9 }, (_, index) => sample(index + 1)),
    };
    render(<TrainingView client={client} />);

    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training }));
    await waitFor(() => expect(client.send).toHaveBeenCalledWith('GetTrainingSample', expect.objectContaining({ sampleId: 'sample-1' })));

    const firstPageRequest = client.send.mock.calls.find((call) =>
      call[0] === 'GetTrainingSample' && call[1]?.sampleId === 'sample-1');
    expect(firstPageRequest?.[1]?.requestId).toEqual(expect.any(String));

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(client.send).toHaveBeenCalledWith('GetTrainingSample', expect.objectContaining({ sampleId: 'sample-9' })));
    const secondPageRequest = client.send.mock.calls.find((call) =>
      call[0] === 'GetTrainingSample' && call[1]?.sampleId === 'sample-9');
    const secondPageRequestId = secondPageRequest?.[1]?.requestId as string;

    act(() => emit('trainingSamplePreview', {
      gameId: 'game-1', sample: sample(1), imageData: 'data:image/png;base64,stale',
      requestId: firstPageRequest?.[1]?.requestId,
    }));
    expect(document.querySelectorAll('img')).toHaveLength(0);

    act(() => emit('trainingSamplePreview', {
      gameId: 'game-1', sample: sample(9), imageData: 'data:image/png;base64,current',
      requestId: secondPageRequestId,
    }));
    await waitFor(() => expect(document.querySelector('img')).not.toBeNull());
    expect(document.querySelector('img')?.getAttribute('style')).toContain('aspect-ratio: 2000 / 1125');

    fireEvent.click(screen.getByRole('button', { name: /sample-9/ }));
    expect(client.send).toHaveBeenLastCalledWith('GetTrainingSample', expect.objectContaining({
      sampleId: 'sample-9',
      requestId: expect.stringContaining('editor-'),
    }));
    expect(client.send.mock.calls.at(-1)?.[1]).not.toHaveProperty('previewOnly');

    const editorRequestId = client.send.mock.calls.at(-1)?.[1]?.requestId;
    act(() => emit('trainingSample', {
      gameId: 'game-1', sample: sample(9), imageData: 'data:image/png;base64,current', requestId: editorRequestId,
    }));
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Previous' })).toHaveLength(2));

    const navigationPrevious = screen.getAllByRole('button', { name: 'Previous' }).at(-1);
    const navigationNext = screen.getAllByRole('button', { name: 'Next' }).at(-1);
    expect((navigationPrevious as HTMLButtonElement).disabled).toBe(false);
    expect((navigationNext as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(navigationPrevious!);
    expect(client.send).toHaveBeenLastCalledWith('GetTrainingSample', expect.objectContaining({
      sampleId: 'sample-8',
      requestId: expect.stringContaining('editor-'),
    }));
  });

  it('shows an active training status and disables starting another run', () => {
    const { client, emit } = createClient();
    const training: TrainingMessage = {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [{ ...sample(1), labels: [{ classId: 0, centerX: 0.5, centerY: 0.5, width: 0.1, height: 0.1 }] }],
      model: { inputWidth: 640, inputHeight: 640 },
    };
    render(<TrainingView client={client} />);

    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training }));
    act(() => emit('trainingProgress', {
      gameId: 'game-1',
      status: 'started',
      message: 'Training is running in a console window.',
    }));

    expect(screen.getByText('Training in progress')).toBeTruthy();
    expect(screen.getByText(/actual device is shown in the console/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Start training' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Cancel' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('shows exported image totals, per-event coverage, and coverage warnings', () => {
    const { client, emit } = createClient();
    const training: TrainingMessage = {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [{ ...sample(1), labels: [{ classId: 0, centerX: 0.5, centerY: 0.5, width: 0.1, height: 0.1 }] }],
      dataset: {
        trainingImages: 3,
        validationImages: 0,
        eventCoverage: [{ classId: 0, name: 'Event', sampleCount: 1, trainingSamples: 1, validationSamples: 0 }],
        warnings: ["'Event' has one labeled frame; it is train-only and cannot be validated."],
      },
    };
    render(<TrainingView client={client} />);

    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training }));

    expect(screen.getByText('Train images').nextElementSibling?.textContent).toBe('3');
    expect(screen.getByText('Validation images').nextElementSibling?.textContent).toBe('0');
    expect(screen.getByText(/1 train \/ 0 validation frames/)).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toContain('train-only');
  });

  it('combines invalid filtering with search and allows training from valid labeled samples', () => {
    const { client, emit } = createClient();
    const training: TrainingMessage = {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [
        { ...sample(1), labels: [{ classId: 0, centerX: 0.5, centerY: 0.5, width: 0.1, height: 0.1 }] },
        { ...sample(2), labels: [{ classId: 0, centerX: 0.9, centerY: 0.9, width: 0.1, height: 0.1 }] },
      ],
      invalidSamples: [{ id: 'sample-2', reason: 'Event label is outside its region' }],
    };
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training }));

    expect(screen.getByText(/1 invalid sample will be skipped/)).toBeTruthy();
    expect((screen.getByRole('button', { name: 'Start training' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.change(screen.getByLabelText('Sample validity'), { target: { value: 'invalid' } });
    expect(screen.queryByRole('button', { name: /sample-1/ })).toBeNull();
    expect(screen.getByRole('button', { name: /sample-2/ }).textContent).toContain('outside its region');
    fireEvent.change(screen.getByLabelText('Filter samples'), { target: { value: 'sample-1' } });
    expect(screen.getByText('No samples match this filter')).toBeTruthy();
  });

  it('persists group creation and event membership through their respective commands', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1', events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }], samples: [],
    } satisfies TrainingMessage }));

    fireEvent.change(screen.getByLabelText('New region group name'), { target: { value: 'HUD' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create group' }));
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingRegionGroups', expect.objectContaining({
      regionGroups: [expect.objectContaining({ id: 1, name: 'HUD' })],
    }));
    const transferred = new Map<string, string>();
    const dataTransfer = {
      effectAllowed: 'none',
      dropEffect: 'none',
      setData: (type: string, value: string) => transferred.set(type, value),
      getData: (type: string) => transferred.get(type) ?? '',
    };
    const eventRow = screen.getByText('Event').closest('.training-tree-event')!;
    const groupFolder = screen.getByText('HUD').closest('.training-event-folder')!;
    fireEvent.dragStart(eventRow, { dataTransfer });
    fireEvent.drop(groupFolder, { dataTransfer });
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingEvents', expect.objectContaining({
      events: [expect.objectContaining({ regionGroupId: 1 })],
    }));
  });

  it('deletes an event optimistically and clears the busy state once saved', () => {
    const { client, emit } = createClient();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [
        { id: 1, classId: 0, name: 'Delete Me', type: 'Trigger' },
        { id: 2, classId: 1, name: 'Keep', type: 'Trigger' },
      ],
      samples: [],
    } satisfies TrainingMessage }));

    const row = screen.getByText('Delete Me').closest('.training-tree-event')!;
    fireEvent.click(within(row as HTMLElement).getByRole('button', { name: 'Delete' }));
    expect(confirm).toHaveBeenCalled();
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingEvents', expect.objectContaining({
      events: [expect.objectContaining({ id: 2, name: 'Keep' })],
    }));

    act(() => emit('trainingProgress', { gameId: 'game-1', status: 'eventDeleteProgress', message: 'x 50%', percent: 50 }));
    act(() => emit('trainingProgress', { gameId: 'game-1', status: 'eventsUpdated', message: 'Training events updated.' }));
    act(() => emit('training', { training: {
      gameId: 'game-1', events: [{ id: 2, classId: 0, name: 'Keep', type: 'Trigger' }], samples: [],
    } satisfies TrainingMessage }));
    expect(screen.queryByText(/Deleting Delete Me event/)).toBeNull();
  });

  it('loads a labeled sample for the event whose region is being edited', async () => {
    const { client, emit } = createClient();
    const samples = Array.from({ length: 9 }, (_, index) => ({
      ...sample(index + 1),
      labels: [{
        classId: index === 8 ? 1 : 0,
        centerX: 0.5, centerY: 0.5, width: 0.1, height: 0.1,
      }],
    }));
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [
        { id: 1, classId: 0, name: 'First', type: 'Trigger' },
        { id: 2, classId: 1, name: 'Second', type: 'Trigger' },
      ],
      samples,
    } satisfies TrainingMessage }));

    const secondRow = screen.getByText('Second').closest('.training-tree-event')!;
    fireEvent.click(within(secondRow as HTMLElement).getByRole('button', { name: 'Region' }));
    await waitFor(() => expect(client.send).toHaveBeenCalledWith('GetTrainingSample', expect.objectContaining({
      sampleId: 'sample-9', previewOnly: true,
    })));
    const request = client.send.mock.calls.find((call) =>
      call[0] === 'GetTrainingSample' && call[1]?.sampleId === 'sample-9' && call[1]?.previewOnly);
    act(() => emit('trainingSamplePreview', {
      gameId: 'game-1', sample: samples[8], imageData: 'data:image/png;base64,second-event',
      requestId: request?.[1]?.requestId,
    }));

    await waitFor(() => expect(screen.getByAltText('Training frame region background')
      .getAttribute('src')).toBe('data:image/png;base64,second-event'));
  });
});

describe('TrainingView training preferences', () => {
  afterEach(cleanup);

  it('restores the saved preferences for a game and keeps them on later re-pushes', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    const base: TrainingMessage = {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
    };

    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      ...base, preferences: { epochs: 42, device: 'rocm', augmentCopies: 2 },
    } }));

    expect((screen.getByLabelText('Epochs') as HTMLInputElement).value).toBe('42');
    expect((screen.getByLabelText('Device') as HTMLSelectElement).value).toBe('rocm');
    expect((screen.getByLabelText(/^Augmentation/) as HTMLSelectElement).value).toBe('2');

    fireEvent.change(screen.getByLabelText('Epochs'), { target: { value: '7' } });
    expect((screen.getByLabelText('Epochs') as HTMLInputElement).value).toBe('7');

    act(() => emit('training', { training: {
      ...base, preferences: { epochs: 42, device: 'rocm', augmentCopies: 2 },
    } }));
    expect((screen.getByLabelText('Epochs') as HTMLInputElement).value).toBe('7');
  });

  it('falls back to the defaults when switching to a game without saved preferences', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);

    act(() => emit('gameList', [{ id: 'game-1', name: 'One' }, { id: 'game-2', name: 'Two' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      preferences: { epochs: 42, device: 'rocm', augmentCopies: 2 },
    } }));
    expect((screen.getByLabelText('Epochs') as HTMLInputElement).value).toBe('42');

    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'game-2' } });
    act(() => emit('training', { training: {
      gameId: 'game-2',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
    } }));

    expect((screen.getByLabelText('Epochs') as HTMLInputElement).value).toBe('100');
    expect((screen.getByLabelText('Device') as HTMLSelectElement).value).toBe('auto');
    expect((screen.getByLabelText(/^Augmentation/) as HTMLSelectElement).value).toBe('0');
  });
});

describe('TrainingView training run feedback', () => {
  afterEach(cleanup);

  it('shows the dataset-prep modal while exporting and cancels the run from it', async () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
    } satisfies TrainingMessage }));
    expect(screen.queryByRole('dialog', { name: 'Preparing training data' })).toBeNull();

    act(() => emit('trainingProgress', {
      gameId: 'game-1', status: 'exporting', message: 'Preparing the training dataset.',
    }));
    // The overlay waits a short delay before appearing, so poll for it.
    const overlay = await screen.findByRole('dialog', { name: 'Preparing training data' });

    fireEvent.click(within(overlay).getByRole('button', { name: 'Cancel' }));
    expect(client.send).toHaveBeenCalledWith('CancelTraining');
  });

  it('shows the dataset-prep modal for a client that connects mid-run via the training phase', async () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      trainingActive: true,
      trainingPhase: 'exporting',
    } satisfies TrainingMessage }));

    expect(await screen.findByRole('dialog', { name: 'Preparing training data' })).toBeTruthy();
  });

  it('shows epoch progress and metrics while the model trains without blocking the view', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      trainingActive: true,
      trainingPhase: 'training',
    } satisfies TrainingMessage }));
    act(() => emit('trainingProgress', {
      gameId: 'game-1',
      status: 'progress',
      message: 'Epoch 12/100 · loss 0.8321 · mAP50 0.4231',
      percent: 12,
      details: { epoch: 12, epochs: 100, loss: 0.8321, map50: 0.4231 },
    }));

    expect(screen.getByText('Training in progress')).toBeTruthy();
    // The epoch counter lives in the panel heading.
    expect(screen.getByText('Epoch 12/100')).toBeTruthy();
    const bar = screen.getByRole('progressbar');
    expect(bar.getAttribute('aria-valuenow')).toBe('12');
    // Key numbers render as separate cells inside the panel.
    expect(screen.getByText('loss 0.8321')).toBeTruthy();
    expect(screen.getByText('mAP50 0.4231')).toBeTruthy();
    // The per-epoch message itself no longer prints in the bottom progress line while active.
    expect(screen.queryByText('Epoch 12/100 · loss 0.8321 · mAP50 0.4231')).toBeNull();
    // The view stays interactive while the model trains: no dataset-prep modal, start disabled.
    expect(screen.queryByRole('dialog', { name: 'Preparing training data' })).toBeNull();
    expect((screen.getByRole('button', { name: 'Start training' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('plots each epoch in the sparkline and paces the estimate from heartbeat times', () => {
    const { client, emit } = createClient();
    // Deterministic heartbeat cadence: every epoch finishes 10s after the previous one.
    const clock = vi.spyOn(Date, 'now');
    let now = 1_000_000;
    clock.mockImplementation(() => (now += 10_000));
    try {
      render(<TrainingView client={client} />);
      act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
      act(() => emit('training', { training: {
        gameId: 'game-1',
        events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
        samples: [],
        trainingActive: true,
        trainingPhase: 'training',
      } satisfies TrainingMessage }));
      for (const [epoch, loss, map50] of [
        [1, 1.0, null],
        [2, 0.8, 0.5],
        [3, 0.6, 0.7],
      ] as const) {
        act(() => emit('trainingProgress', {
          gameId: 'game-1',
          status: 'progress',
          message: `Epoch ${epoch}/4`,
          details: { epoch, epochs: 4, loss, map50 },
        }));
      }

      const chart = screen.getByRole('img', { name: 'Training metrics per epoch' });
      // One polyline per available series: loss from epoch 1, mAP50 from epoch 2.
      expect(chart.querySelectorAll('polyline').length).toBe(2);
      expect(chart.querySelector('.training-sparkline-fill')).toBeTruthy();
      expect(chart.querySelectorAll('.training-sparkline-grid').length).toBe(4);
      expect(chart.querySelectorAll('.training-sparkline-tick').length).toBe(2);
      expect(screen.getByText('Epoch 3/4')).toBeTruthy();
      expect(screen.getByText('mAP50')).toBeTruthy();
      // Three completed epochs at ~10s each, one of four left.
      expect(screen.getByText('30s elapsed')).toBeTruthy();
      expect(screen.getByText('~10s remaining')).toBeTruthy();
    } finally {
      clock.mockRestore();
    }
  });

  it('shows runner notes in the panel while active and only terminal messages at the bottom', () => {
    const { client, emit } = createClient();
    const view = render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      trainingActive: true,
      trainingPhase: 'training',
    } satisfies TrainingMessage }));
    act(() => emit('trainingProgress', {
      gameId: 'game-1',
      status: 'progress',
      message: 'Dataset exported: 84 train and 21 validation frames.',
    }));

    expect(screen.getByText('Dataset exported: 84 train and 21 validation frames.')).toBeTruthy();
    // While the run is active nothing prints in the bottom progress line.
    expect(view.container.querySelector('.training-progress')).toBeNull();

    act(() => emit('trainingProgress', {
      gameId: 'game-1', status: 'completed', message: 'Installed models/overwatch.onnx.',
    }));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      trainingActive: false,
    } satisfies TrainingMessage }));

    expect(screen.queryByText('Training in progress')).toBeNull();
    const bottom = view.container.querySelector('.training-progress');
    expect(bottom?.textContent).toContain('Installed models/overwatch.onnx.');
  });

  it('renders no graph or legend while the heartbeats carry no metric values', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'Game' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
      trainingActive: true,
      trainingPhase: 'training',
    } satisfies TrainingMessage }));
    for (const epoch of [1, 2]) {
      act(() => emit('trainingProgress', {
        gameId: 'game-1',
        status: 'progress',
        message: `Epoch ${epoch}/4`,
        details: { epoch, epochs: 4, loss: null, map50: null },
      }));
    }

    expect(screen.queryByRole('img', { name: 'Training metrics per epoch' })).toBeNull();
    expect(screen.queryByText('loss')).toBeNull();
    // Pacing still works off the heartbeat times alone.
    expect(screen.getByText(/remaining/)).toBeTruthy();
  });

  it('clears stale progress state when switching games', () => {
    const { client, emit } = createClient();
    render(<TrainingView client={client} />);
    act(() => emit('gameList', [{ id: 'game-1', name: 'One' }, { id: 'game-2', name: 'Two' }]));
    act(() => emit('training', { training: {
      gameId: 'game-1',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
    } satisfies TrainingMessage }));
    act(() => emit('trainingProgress', {
      gameId: 'game-1', status: 'progress', message: 'Epoch 1/10',
    }));
    expect(screen.getByText('Epoch 1/10')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Game'), { target: { value: 'game-2' } });
    act(() => emit('training', { training: {
      gameId: 'game-2',
      events: [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' }],
      samples: [],
    } satisfies TrainingMessage }));
    expect(screen.queryByText('Epoch 1/10')).toBeNull();
  });
});
