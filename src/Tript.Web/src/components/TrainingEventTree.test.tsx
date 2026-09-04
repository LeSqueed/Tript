import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { TrainingEventTree } from './TrainingEventTree';

describe('TrainingEventTree', () => {
  afterEach(cleanup);

  it('moves a grouped event into the Ungrouped folder by drag and drop', () => {
    const onMove = vi.fn();
    render(
      <TrainingEventTree
        events={[{ id: 1, classId: 0, name: 'Elimination', type: 'Trigger', regionGroupId: 7 }]}
        groups={[{
          id: 7, name: 'Event Feed', screenRegionX: 0.2, screenRegionY: 0.2,
          screenRegionW: 0.5, screenRegionH: 0.3,
        }]}
        onEdit={vi.fn()}
        onRegion={vi.fn()}
        onMove={onMove}
      />,
    );

    const transferred = new Map<string, string>();
    const dataTransfer = {
      effectAllowed: 'none',
      dropEffect: 'none',
      setData: (type: string, value: string) => transferred.set(type, value),
      getData: (type: string) => transferred.get(type) ?? '',
    };
    const eventRow = screen.getByText('Elimination').closest('.training-tree-event')!;
    const ungrouped = screen.getByLabelText('Ungrouped events').closest('.training-event-folder')!;
    fireEvent.dragStart(eventRow, { dataTransfer });
    fireEvent.drop(ungrouped, { dataTransfer });

    expect(onMove).toHaveBeenCalledWith(1, null);
  });

  it.each([
    ['an external file', { getData: () => '', files: [{ name: 'events.json' }] }],
    ['external text', { getData: (type: string) => type === 'text/plain' ? '0' : '' }],
  ])('ignores %s dropped on a folder', (_, dataTransfer) => {
    const onMove = vi.fn();
    render(
      <TrainingEventTree
        events={[{ id: 0, classId: 0, name: 'Elimination', type: 'Trigger' }]}
        groups={[]}
        onEdit={vi.fn()}
        onRegion={vi.fn()}
        onMove={onMove}
      />,
    );

    fireEvent.drop(screen.getByLabelText('Ungrouped events').closest('.training-event-folder')!, { dataTransfer });
    expect(onMove).not.toHaveBeenCalled();
  });

  it('accepts event id zero when it is present in the internal drag payload', () => {
    const onMove = vi.fn();
    render(
      <TrainingEventTree
        events={[{ id: 0, classId: 0, name: 'Elimination', type: 'Trigger' }]}
        groups={[]}
        onEdit={vi.fn()}
        onRegion={vi.fn()}
        onMove={onMove}
      />,
    );
    const transferred = new Map<string, string>();
    const dataTransfer = {
      effectAllowed: 'none',
      dropEffect: 'none',
      setData: (type: string, value: string) => transferred.set(type, value),
      getData: (type: string) => transferred.get(type) ?? '',
    };

    fireEvent.dragStart(screen.getByText('Elimination').closest('.training-tree-event')!, { dataTransfer });
    fireEvent.drop(screen.getByLabelText('Ungrouped events').closest('.training-event-folder')!, { dataTransfer });
    expect(onMove).toHaveBeenCalledWith(0, null);
  });

  it('renders the actions row under the event name and no folder select', () => {
    const onEdit = vi.fn();
    const onRegion = vi.fn();
    render(
      <TrainingEventTree
        events={[{ id: 0, classId: 0, name: 'Elimination', type: 'Trigger' }]}
        groups={[{
          id: 7, name: 'Event Feed', screenRegionX: null, screenRegionY: null,
          screenRegionW: null, screenRegionH: null,
        }]}
        onEdit={onEdit}
        onRegion={onRegion}
        onMove={vi.fn()}
      />,
    );

    expect(screen.queryByRole('combobox')).toBeNull();

    const row = screen.getByText('Elimination').closest('.training-tree-event')!;
    const name = row.querySelector<HTMLElement>('.training-tree-event-copy strong')!;
    const actions = row.querySelector<HTMLElement>('.training-tree-event-actions')!;
    expect(name.compareDocumentPosition(actions) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    fireEvent.click(within(actions).getByRole('button', { name: 'Edit' }));
    expect(onEdit).toHaveBeenCalledWith(expect.objectContaining({ id: 0, name: 'Elimination' }));
    fireEvent.click(within(actions).getByRole('button', { name: 'Region' }));
    expect(onRegion).toHaveBeenCalledWith(expect.objectContaining({ id: 0, name: 'Elimination' }));
  });
});
