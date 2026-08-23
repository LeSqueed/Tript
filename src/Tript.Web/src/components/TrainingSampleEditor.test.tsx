import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TrainingSampleEditor } from './TrainingSampleEditor';
import type { IpcClient } from '../ipc/websocketClient';
import type { TrainingSampleMessage } from '../ipc/protocol';

const sample: TrainingSampleMessage = {
  gameId: 'game-1',
  sample: {
    id: 'sample-1',
    imageFile: 'sample-1.png',
    sourcePath: 'C:/capture.mp4',
    timestampSeconds: 1,
    imageWidth: 2000,
    imageHeight: 1125,
    labels: [{ classId: 0, centerX: 0.3, centerY: 0.3, width: 0.1, height: 0.1 }],
  },
  imageData: 'data:image/png;base64,image',
};

const events = [{ id: 1, classId: 0, name: 'Event', type: 'Trigger' as const }];

function createClient() {
  return {
    state: 'connected' as const,
    on: vi.fn(() => () => undefined),
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => undefined),
  } as unknown as IpcClient & { send: ReturnType<typeof vi.fn> };
}

function makeDirty() {
  const canvas = document.querySelector('.training-editor-canvas') as HTMLElement;
  vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({
    left: 0, top: 0, width: 100, height: 100,
    right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}),
  });
  const box = document.querySelector('.training-box') as HTMLElement;
  Object.defineProperty(box, 'setPointerCapture', { value: vi.fn() });
  fireEvent.pointerDown(box, { clientX: 30, clientY: 30, pointerId: 1 });
  fireEvent.pointerMove(canvas, { clientX: 50, clientY: 50, pointerId: 1 });
}

describe('TrainingSampleEditor save lifecycle', () => {
  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
  });

  it('keeps the modal open and shows confirmation after saving labels', () => {
    const client = createClient();
    render(<TrainingSampleEditor client={client} gameId="game-1" sample={sample} events={events} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.getByRole('status').textContent).toContain('Labels saved');
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingSample', expect.objectContaining({ sampleId: 'sample-1' }));
  });

  it('warns before closing dirty labels and discards only after confirmation', () => {
    const onClose = vi.fn();
    render(<TrainingSampleEditor client={createClient()} gameId="game-1" sample={sample} events={events} onClose={onClose} />);
    makeDirty();

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    fireEvent.click(screen.getByRole('button', { name: 'Close label frame' }));
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('unsaved changes'));
    expect(onClose).not.toHaveBeenCalled();

    confirm.mockReturnValue(true);
    fireEvent.click(screen.getByRole('button', { name: 'Close label frame' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('warns before navigating dirty labels and does not save discarded edits', () => {
    const onNavigate = vi.fn();
    const client = createClient();
    render(
      <TrainingSampleEditor
        client={client}
        gameId="game-1"
        sample={sample}
        events={events}
        onNavigate={onNavigate}
        canNavigateNext
        onClose={vi.fn()}
      />,
    );
    makeDirty();

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onNavigate).not.toHaveBeenCalled();
    expect(client.send).not.toHaveBeenCalledWith('UpdateTrainingSample', expect.anything());

    confirm.mockReturnValue(true);
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onNavigate).toHaveBeenCalledWith('next');
    expect(client.send).not.toHaveBeenCalledWith('UpdateTrainingSample', expect.anything());
  });

  it('adds non-overlapping model suggestions as editable labels', () => {
    let suggestionHandler: ((content: unknown) => void) | undefined;
    const client = createClient();
    client.on = vi.fn((method: string, handler: (content: unknown) => void) => {
      if (method === 'trainingLabelSuggestions') suggestionHandler = handler;
      return () => undefined;
    }) as typeof client.on;
    render(<TrainingSampleEditor client={client} gameId="game-1" sample={sample} events={events} hasModel onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole('button', { name: 'Suggest labels' }));
    const request = client.send.mock.calls.find(([method]) => method === 'SuggestTrainingLabels');
    expect(request).toBeTruthy();

    act(() => suggestionHandler?.({
      gameId: 'game-1',
      sampleId: 'sample-1',
      requestId: request?.[1]?.requestId,
      suggestions: [{
        label: { classId: 0, centerX: 0.7, centerY: 0.7, width: 0.1, height: 0.1 },
        confidence: 0.85,
      }],
    }));

    expect(screen.getByText(/85%/)).toBeTruthy();
    expect(screen.getByText('2 labels on this frame')).toBeTruthy();
  });
});
