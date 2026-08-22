import { useState } from 'react';
import type { TrainingEventDefinition } from '../ipc/protocol';
import { Button, Field, SelectField, TextField } from './ui/controls';
import { useTrainingDialog } from './useTrainingDialog';

interface TrainingEventEditorProps {
  event: TrainingEventDefinition;
  isNew: boolean;
  onCancel(): void;
  onSave(event: TrainingEventDefinition): void;
}

const BOOKMARK_TYPES = ['Manual', 'Kill', 'Goal', 'Assist', 'Death'];

export function TrainingEventEditor({ event, isNew, onCancel, onSave }: TrainingEventEditorProps) {
  const [name, setName] = useState(event.name);
  const [type, setType] = useState<TrainingEventDefinition['type']>(event.type);
  const [bookmarkType, setBookmarkType] = useState(event.bookmarkType ?? '');
  const [lifetimeMs, setLifetimeMs] = useState(event.lifetimeMs == null ? '' : String(event.lifetimeMs));
  const [validationError, setValidationError] = useState<string | null>(null);
  const dialogRef = useTrainingDialog<HTMLElement>(onCancel);

  const save = () => {
    const trimmedName = name.trim();
    const parsedLifetime = lifetimeMs === '' ? null : Number(lifetimeMs);
    if (!trimmedName) {
      setValidationError('Event name is required.');
      return;
    }
    if (parsedLifetime !== null && (!Number.isInteger(parsedLifetime) || parsedLifetime < 0)) {
      setValidationError('Lifetime must be a whole number of milliseconds.');
      return;
    }

    setValidationError(null);
    onSave({
      ...event,
      name: trimmedName,
      type,
      bookmarkType: bookmarkType || null,
      lifetimeMs: parsedLifetime,
    });
  };

  return (
    <div className="training-event-overlay" role="presentation">
      <section ref={dialogRef} tabIndex={-1} className="training-event-dialog" role="dialog" aria-modal="true" aria-labelledby="training-event-title">
        <div className="training-palette-heading">
          <div>
            <p className="training-eyebrow">Event palette</p>
            <h3 id="training-event-title">{isNew ? 'New event' : 'Edit event'}</h3>
          </div>
          <Button variant="ghost" size="small" onClick={onCancel} aria-label="Cancel event edit">Cancel</Button>
        </div>
        <Field label="Name">
          <TextField value={name} onChange={setName} autoFocus />
        </Field>
        <div className="training-event-form-grid">
          <Field label="Type">
            <SelectField
              value={type}
              onChange={(value) => setType(value as TrainingEventDefinition['type'])}
              options={[{ value: 'Trigger', label: 'Trigger' }, { value: 'Exclusion', label: 'Exclusion' }]}
            />
          </Field>
          <Field label="Bookmark">
            <SelectField
              value={bookmarkType}
              onChange={setBookmarkType}
              options={[{ value: '', label: 'No bookmark' }, ...BOOKMARK_TYPES.map((value) => ({ value, label: value }))]}
            />
          </Field>
        </div>
        <Field label="Lifetime (ms)" hint="Optional cooldown for this event.">
          <TextField
            type="number"
            min={0}
            step={1}
            value={lifetimeMs}
            onChange={(value) => { setLifetimeMs(value); setValidationError(null); }}
            aria-invalid={validationError !== null}
          />
        </Field>
        {validationError && <p className="training-event-error" role="alert">{validationError}</p>}
        <div className="training-event-dialog-actions">
          <Button variant="ghost" onClick={onCancel}>Cancel</Button>
          <Button onClick={save} disabled={!name.trim()}>Save event</Button>
        </div>
      </section>
    </div>
  );
}
