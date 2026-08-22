import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import type { TrainingEventDefinition } from '../ipc/protocol';

const source: TrainingEventDefinition = {
  id: 1,
  classId: 1,
  name: 'Scoreboard',
  type: 'Trigger',
  screenRegionX: 0.1,
  screenRegionY: 0.2,
  screenRegionW: 0.3,
  screenRegionH: 0.4,
};

describe('TrainingRegionEditor', () => {
  afterEach(cleanup);

  it('copies normalized coordinates from another event without changing target fields', () => {
    const target: TrainingEventDefinition = {
      id: 2,
      classId: 2,
      name: 'Round end',
      type: 'Exclusion',
      bookmarkType: null,
    };
    const onSave = vi.fn();
    render(<TrainingRegionEditor event={target} events={[source, target]} onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.change(screen.getByLabelText('Copy region from event'), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Copy region' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save region' }));

    expect(onSave).toHaveBeenCalledWith({
      ...target,
      screenRegionX: 0.1,
      screenRegionY: 0.2,
      screenRegionW: 0.3,
      screenRegionH: 0.4,
    });
  });

  it('clears a saved region as null coordinates', () => {
    const onSave = vi.fn();
    render(<TrainingRegionEditor event={source} events={[source]} onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.click(screen.getByRole('button', { name: 'Clear region' }));
    fireEvent.click(screen.getByRole('button', { name: 'Save region' }));

    expect(onSave).toHaveBeenCalledWith({
      ...source,
      screenRegionX: null,
      screenRegionY: null,
      screenRegionW: null,
      screenRegionH: null,
    });
  });
});
