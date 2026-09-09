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
  const handlers = new Map<string, Set<(content: unknown) => void>>();
  const client = {
    state: 'connected' as const,
    on: vi.fn((method: string, handler: (content: unknown) => void) => {
      const listeners = handlers.get(method) ?? new Set();
      listeners.add(handler);
      handlers.set(method, listeners);
      return () => listeners.delete(handler);
    }),
    send: vi.fn(),
    connect: vi.fn(),
    close: vi.fn(),
    onStateChange: vi.fn(() => () => undefined),
    emit(method: string, content: unknown) {
      for (const handler of handlers.get(method) ?? []) handler(content);
    },
  } as unknown as IpcClient & {
    send: ReturnType<typeof vi.fn>;
    emit(method: string, content: unknown): void;
  };
  return client;
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

  it('keeps labels dirty until the matching server confirmation arrives', () => {
    const client = createClient();
    const onClose = vi.fn();
    render(<TrainingSampleEditor client={client} gameId="game-1" sample={sample} events={events} onClose={onClose} />);
    makeDirty();

    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));

    expect(screen.getByRole('dialog')).toBeTruthy();
    expect(screen.queryByText('Labels saved')).toBeNull();
    expect((screen.getByRole('button', { name: 'Saving...' }) as HTMLButtonElement).disabled).toBe(true);
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingSample', expect.objectContaining({ sampleId: 'sample-1' }));
    const requestId = client.send.mock.calls.find(([method]) => method === 'UpdateTrainingSample')?.[1]?.requestId;

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    fireEvent.click(screen.getByRole('button', { name: 'Close label frame' }));
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('unsaved changes'));
    expect(onClose).not.toHaveBeenCalled();

    act(() => client.emit('trainingSampleUpdateResult', { requestId: 'another-window-request', success: true }));
    act(() => client.emit('error', { message: 'An unrelated action failed.' }));
    expect(screen.queryByText('Labels saved')).toBeNull();
    expect(screen.getByRole('button', { name: 'Saving...' })).toBeTruthy();

    act(() => client.emit('trainingSampleUpdateResult', { requestId, success: true }));
    expect(screen.getByRole('status').textContent).toContain('Labels saved');
    fireEvent.click(screen.getByRole('button', { name: 'Close label frame' }));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  function drawOcrRegion() {
    const canvas = document.querySelector('.training-editor-canvas') as HTMLElement;
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({
      left: 0, top: 0, width: 100, height: 100,
      right: 100, bottom: 100, x: 0, y: 0, toJSON: () => ({}),
    });
    Object.defineProperty(canvas, 'setPointerCapture', { value: vi.fn(), configurable: true });
    fireEvent.pointerDown(canvas, { clientX: 20, clientY: 40, pointerId: 1 });
    fireEvent.pointerMove(canvas, { clientX: 60, clientY: 70, pointerId: 1 });
    fireEvent.pointerUp(canvas, { clientX: 60, clientY: 70, pointerId: 1 });
  }

  it('draws a free OCR region and saves its text without creating an object label', () => {
    const client = createClient();
    render(<TrainingSampleEditor
      client={client}
      gameId="game-1"
      sample={{ ...sample, sample: { ...sample.sample, labels: [], ocrRegions: [] } }}
      events={events}
      onClose={vi.fn()}
    />);

    fireEvent.click(screen.getByRole('button', { name: 'Add OCR region' }));
    drawOcrRegion();
    fireEvent.change(screen.getByLabelText('Text'), { target: { value: 'ELIMINATED AMON' } });
    fireEvent.click(screen.getByRole('button', { name: /^Save$/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));

    const call = client.send.mock.calls.find(([method]) => method === 'UpdateTrainingSample');
    expect(call?.[1].labels).toEqual([]);
    expect(call?.[1].ocrRegions).toHaveLength(1);
    expect(call?.[1].ocrRegions[0]).toMatchObject({ text: 'ELIMINATED AMON' });
    expect(call?.[1].ocrRegions[0].x).toBeCloseTo(0.2, 5);
    expect(call?.[1].ocrRegions[0].y).toBeCloseTo(0.4, 5);
    expect(call?.[1].ocrRegions[0].width).toBeCloseTo(0.4, 5);
    expect(call?.[1].ocrRegions[0].height).toBeCloseTo(0.3, 5);
  });

  it('edits and removes an OCR region from its caption', () => {
    const client = createClient();
    render(<TrainingSampleEditor
      client={client}
      gameId="game-1"
      sample={{
        ...sample,
        sample: {
          ...sample.sample,
          labels: [],
          ocrRegions: [
            { x: 0.1, y: 0.1, width: 0.3, height: 0.08, text: 'UPPER TEXT' },
            { x: 0.1, y: 0.5, width: 0.3, height: 0.08, text: 'LOWER TEXT' },
          ],
        },
      }}
      events={events}
      onClose={vi.fn()}
    />);

    fireEvent.click(screen.getByRole('button', { name: 'LOWER TEXT' }));
    expect((screen.getByLabelText('Text') as HTMLInputElement).value).toBe('LOWER TEXT');
    fireEvent.change(screen.getByLabelText('Text'), { target: { value: 'UPDATED LOWER' } });
    fireEvent.click(screen.getByRole('button', { name: /^Save$/ }));
    fireEvent.click(screen.getByRole('button', { name: 'UPDATED LOWER' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));

    const call = client.send.mock.calls.find(([method]) => method === 'UpdateTrainingSample');
    expect(call?.[1].ocrRegions).toEqual([
      { x: 0.1, y: 0.1, width: 0.3, height: 0.08, text: 'UPPER TEXT' },
    ]);
  });

  it('reports a failed save and keeps the unsaved-change guard active', () => {
    const client = createClient();
    const onClose = vi.fn();
    render(<TrainingSampleEditor client={client} gameId="game-1" sample={sample} events={events} onClose={onClose} />);
    makeDirty();
    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));
    const requestId = client.send.mock.calls.find(([method]) => method === 'UpdateTrainingSample')?.[1]?.requestId;

    act(() => client.emit('trainingSampleUpdateResult', {
      requestId, success: false, error: 'The sample could not be written.',
    }));
    expect(screen.getByRole('alert').textContent).toContain('could not be written');
    expect(screen.getByRole('button', { name: 'Save labels' })).toBeTruthy();
    expect(screen.queryByText('Labels saved')).toBeNull();

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    fireEvent.click(screen.getByRole('button', { name: 'Close label frame' }));
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('unsaved changes'));
    expect(onClose).not.toHaveBeenCalled();
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

  it('marks out-of-region labels but still allows saving them', () => {
    const client = createClient();
    render(
      <TrainingSampleEditor
        client={client}
        gameId="game-1"
        sample={sample}
        events={[{ ...events[0], regionGroupId: 7 }]}
        regionGroups={[{
          id: 7, name: 'Corner', screenRegionX: 0.6, screenRegionY: 0.6,
          screenRegionW: 0.3, screenRegionH: 0.3,
        }]}
        onClose={vi.fn()}
      />,
    );

    expect(screen.getByRole('alert').textContent).toContain('will be skipped when training');
    expect(document.querySelector('.training-box.invalid')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));
    expect(client.send).toHaveBeenCalledWith('UpdateTrainingSample', expect.objectContaining({
      sampleId: 'sample-1',
      labels: [expect.objectContaining({ classId: 0 })],
    }));
  });

  it('shows the effective region for the first existing label when opened', () => {
    const labeledSample = {
      ...sample,
      sample: {
        ...sample.sample,
        labels: [{ classId: 4, centerX: 0.16, centerY: 0.05, width: 0.1, height: 0.03 }],
      },
    };
    render(
      <TrainingSampleEditor
        client={createClient()}
        gameId="game-1"
        sample={labeledSample}
        events={[
          { id: 1, classId: 0, name: 'Death', type: 'Trigger' },
          {
            id: 2, classId: 4, name: 'Death Spectating', type: 'Exclusion',
            screenRegionX: 0.095, screenRegionY: 0.022, screenRegionW: 0.133, screenRegionH: 0.052,
          },
        ]}
        onClose={vi.fn()}
      />,
    );

    const region = screen.getByLabelText('Effective region for Death Spectating');
    expect(Number.parseFloat(region.style.left)).toBeCloseTo(9.5);
    expect(Number.parseFloat(region.style.top)).toBeCloseTo(2.2);
  });

  it('adds a fixed-position label from its canonical event geometry', () => {
    const client = createClient();
    render(
      <TrainingSampleEditor
        client={client}
        gameId="game-1"
        sample={{ ...sample, sample: { ...sample.sample, labels: [] } }}
        events={[{
          id: 1, classId: 4, name: 'Death Spectating', type: 'Exclusion', fixedPosition: true,
          fixedLabelCenterX: 0.16, fixedLabelCenterY: 0.05,
          fixedLabelWidth: 0.11, fixedLabelHeight: 0.03,
        }]}
        onClose={vi.fn()}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Add fixed label for Death Spectating' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save labels' }));

    expect(client.send).toHaveBeenCalledWith('UpdateTrainingSample', {
      gameId: 'game-1',
      sampleId: 'sample-1',
      requestId: expect.any(String),
      labels: [{ classId: 4, centerX: 0.16, centerY: 0.05, width: 0.11, height: 0.03 }],
      ocrRegions: [],
    });
  });

  it('updates membership and edits the selected group region', () => {
    const onEventsChange = vi.fn();
    const onRegionGroupsChange = vi.fn();
    const groups = [{
      id: 7, name: 'HUD', screenRegionX: null, screenRegionY: null,
      screenRegionW: null, screenRegionH: null,
    }];
    render(
      <TrainingSampleEditor
        client={createClient()}
        gameId="game-1"
        sample={sample}
        events={events}
        regionGroups={groups}
        onEventsChange={onEventsChange}
        onRegionGroupsChange={onRegionGroupsChange}
        onClose={vi.fn()}
      />,
    );

    const transferred = new Map<string, string>();
    const dataTransfer = {
      effectAllowed: 'none',
      dropEffect: 'none',
      setData: (type: string, value: string) => transferred.set(type, value),
      getData: (type: string) => transferred.get(type) ?? '',
    };
    const eventRow = screen.getAllByText('Event')
      .find((node) => node.closest('.training-tree-event'))!.closest('.training-tree-event')!;
    const groupFolder = screen.getByLabelText('HUD events').closest('.training-event-folder')!;
    fireEvent.dragStart(eventRow, { dataTransfer });
    fireEvent.drop(groupFolder, { dataTransfer });
    expect(onEventsChange).toHaveBeenCalledWith(
      [expect.objectContaining({ regionGroupId: 7 })],
      expect.any(String),
    );
    fireEvent.click(screen.getAllByRole('button', { name: 'Region' })[0]);
    expect(screen.getByText(/All group members share this region/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save region' }));
    expect(onRegionGroupsChange).toHaveBeenCalledWith(groups, expect.any(String));
  });
});
