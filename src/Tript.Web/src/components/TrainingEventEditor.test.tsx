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
      includeInAutoClips: false,
    });
  });

  it('requires a trigger target for subtractor events', () => {
    const onSave = vi.fn();
    const events: TrainingEventDefinition[] = [
      { id: 1, classId: 0, name: 'Elimination', type: 'Trigger' },
      { id: 2, classId: 1, name: 'Turret', type: 'Subtractor' },
    ];
    render(<TrainingEventEditor event={events[1]} events={events} isNew={false} onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Subtractor' } });
    fireEvent.change(screen.getByLabelText('Subtracts event'), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      type: 'Subtractor',
      bookmarkType: null,
      subtractsEventId: 1,
    }));
  });

  it('saves multiple language-tagged OCR patterns', () => {
    const onSave = vi.fn();
    const event: TrainingEventDefinition = {
      id: 3,
      classId: 2,
      name: 'Turret elimination',
      type: 'Trigger',
    };
    render(<TrainingEventEditor event={event} isNew onCancel={vi.fn()} onSave={onSave} />);

    fireEvent.change(screen.getByLabelText('Detection'), { target: { value: 'Ocr' } });
    fireEvent.change(screen.getByLabelText('Language 1'), { target: { value: 'en-US' } });
    fireEvent.change(screen.getByLabelText('Pattern 1'), {
      target: { value: 'ELIMINATED {player:1..4} SENTRY TURRET' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Add pattern' }));
    fireEvent.change(screen.getByLabelText('Language 2'), { target: { value: 'de-DE' } });
    fireEvent.change(screen.getByLabelText('Pattern 2'), {
      target: { value: 'ELIMINIERT {player:1..4} GESCHUETZ' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({
      detectionKind: 'Ocr',
      classId: -1,
      ocr: expect.objectContaining({
        patterns: [
          expect.objectContaining({ languageTag: 'en-US' }),
          expect.objectContaining({ languageTag: 'de-DE' }),
        ],
      }),
    }));
  });

  it('requires both language and template for every OCR pattern', () => {
    const onSave = vi.fn();
    render(<TrainingEventEditor
      event={{ id: 3, classId: 2, name: 'OCR event', type: 'Trigger' }}
      isNew
      onCancel={vi.fn()}
      onSave={onSave}
    />);

    fireEvent.change(screen.getByLabelText('Detection'), { target: { value: 'Ocr' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(screen.getByRole('alert').textContent).toContain('requires a language and template');
    expect(onSave).not.toHaveBeenCalled();
  });

  function bookmarkOptionValues(): string[] {
    const select = screen.getByLabelText('Bookmark') as HTMLSelectElement;
    return [...select.options].map((option) => option.value);
  }

  it('offers Play but not Manual for automatic events', () => {
    const onSave = vi.fn();
    const event: TrainingEventDefinition = { id: 5, classId: 3, name: 'Vehicle destroyed', type: 'Trigger' };
    render(<TrainingEventEditor event={event} isNew onCancel={vi.fn()} onSave={onSave} />);

    expect(bookmarkOptionValues()).toEqual(['', 'Kill', 'Goal', 'Assist', 'Death', 'Play']);
    fireEvent.change(screen.getByLabelText('Bookmark'), { target: { value: 'Play' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ bookmarkType: 'Play' }));
  });

  it('keeps a legacy Manual bookmark on a trigger until it is changed', () => {
    const onSave = vi.fn();
    const event: TrainingEventDefinition = {
      id: 6, classId: 4, name: 'Old event', type: 'Trigger', bookmarkType: 'Manual',
    };
    render(<TrainingEventEditor event={event} isNew={false} onCancel={vi.fn()} onSave={onSave} />);

    expect(bookmarkOptionValues()).toContain('Manual');
    expect(screen.getByRole('option', { name: 'Manual (legacy)' })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ bookmarkType: 'Manual' }));
  });

  it('drops the bookmark from an exclusion event when it is saved', () => {
    const onSave = vi.fn();
    const event: TrainingEventDefinition = {
      id: -1, classId: -1, name: 'Respawning', type: 'Exclusion', detectionKind: 'Ocr', bookmarkType: 'Manual',
      ocr: { patterns: [{ languageTag: 'en-US', template: 'RESPAWNING' }] },
    };
    render(<TrainingEventEditor event={event} isNew={false} onCancel={vi.fn()} onSave={onSave} />);

    const select = screen.getByLabelText('Bookmark') as HTMLSelectElement;
    expect(select.disabled).toBe(true);
    expect(select.value).toBe('');
    fireEvent.click(screen.getByRole('button', { name: 'Save event' }));

    expect(onSave).toHaveBeenCalledWith(expect.objectContaining({ type: 'Exclusion', bookmarkType: null }));
  });
});
