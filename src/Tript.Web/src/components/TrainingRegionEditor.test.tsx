import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TrainingRegionEditor } from './TrainingRegionEditor';
import type { TrainingEventDefinition, TrainingRegionGroup } from '../ipc/protocol';

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

  it('edits a shared group and explains that members share its region', () => {
    const target: TrainingRegionGroup = {
      id: 2, name: 'HUD', screenRegionX: 0.1, screenRegionY: 0.2, screenRegionW: 0.3, screenRegionH: 0.4,
    };
    const onSave = vi.fn();
    render(<TrainingRegionEditor target={target} targetType="group" onCancel={vi.fn()} onSave={onSave} />);

    expect(screen.getByText(/All group members share this region/)).toBeTruthy();
    expect(screen.queryByLabelText('Copy region from event')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Save region' }));

    expect(onSave).toHaveBeenCalledWith(target);
  });

  it('clears a saved region as null coordinates', () => {
    const onSave = vi.fn();
    render(<TrainingRegionEditor target={source} targetType="event" onCancel={vi.fn()} onSave={onSave} />);

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
