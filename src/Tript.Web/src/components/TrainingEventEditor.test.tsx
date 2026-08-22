import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TrainingEventEditor } from './TrainingEventEditor';
import type { TrainingEventDefinition } from '../ipc/protocol';

describe('TrainingEventEditor', () => {
  afterEach(cleanup);

  it('preserves a null bookmark and region fields when editing an event', () => {
    const event: TrainingEventDefinition = {
      id: 2,
      classId: 4,
      name: 'Round end',
      type: 'Exclusion',
      bookmarkType: null,
      lifetimeMs: 500,
      screenRegionX: 0.1,
      screenRegionY: 0.2,
      screenRegionW: 0.3,
      screenRegionH: 0.4,
    };
    const onSave = vi.fn();
    render(<TrainingEventEditor event={event} isNew={false} onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Round complete' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith({
      ...event,
      name: 'Round complete',
    });
  });

  it('reports fractional lifetimes instead of sending an invalid event', () => {
    const onSave = vi.fn();
    render(<TrainingEventEditor event={{ id: 1, classId: 1, name: 'Kill', type: 'Trigger' }} isNew={false} onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.change(screen.getByRole('spinbutton'), { target: { value: '1.5' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('whole number');
  });
});
