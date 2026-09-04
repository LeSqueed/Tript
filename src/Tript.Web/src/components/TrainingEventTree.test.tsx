import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
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
    const ungrouped = screen.getByText('Ungrouped').closest('.training-event-folder')!;
    fireEvent.dragStart(eventRow, { dataTransfer });
    fireEvent.drop(ungrouped, { dataTransfer });

    expect(onMove).toHaveBeenCalledWith(1, null);
  });
});
