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
