import { useState } from 'react';
import type { TrainingEventDefinition, TrainingOcrPattern } from '../ipc/protocol';
import { Button, Checkbox, Field, SelectField, TextField } from './ui/controls';
import { useTrainingDialog } from './useTrainingDialog';

interface TrainingEventEditorProps {
  event: TrainingEventDefinition;
  events?: TrainingEventDefinition[];
  isNew: boolean;
  onCancel(): void;
  onSave(event: TrainingEventDefinition): void;
}

const BOOKMARK_TYPES = ['Manual', 'Kill', 'Goal', 'Assist', 'Death'];

export function TrainingEventEditor({ event, events = [], isNew, onCancel, onSave }: TrainingEventEditorProps) {
  const [name, setName] = useState(event.name);
  const [type, setType] = useState<TrainingEventDefinition['type']>(event.type);
  const [detectionKind, setDetectionKind] = useState(event.detectionKind ?? 'Object');
  const [ocrPatterns, setOcrPatterns] = useState<TrainingOcrPattern[]>(
    event.ocr?.patterns.length ? event.ocr.patterns : [{ languageTag: 'en-US', template: '' }],
  );
  const [bookmarkType, setBookmarkType] = useState(event.bookmarkType ?? '');
  const [includeInAutoClips, setIncludeInAutoClips] = useState(event.includeInAutoClips === true);
  const [fixedPosition, setFixedPosition] = useState(event.fixedPosition === true);
  const [subtractsEventId, setSubtractsEventId] = useState(
    event.subtractsEventId == null ? '' : String(event.subtractsEventId),
  );
  const [validationError, setValidationError] = useState<string | null>(null);
  const dialogRef = useTrainingDialog<HTMLElement>(onCancel);

  const updatePattern = (index: number, patch: Partial<TrainingOcrPattern>) => {
    setOcrPatterns((current) => current.map((candidate, candidateIndex) =>
      candidateIndex === index ? { ...candidate, ...patch } : candidate));
  };
  const knownLanguages = [...new Set(
    events
      .filter((candidate) => candidate.id !== event.id && candidate.detectionKind === 'Ocr' && candidate.ocr)
      .flatMap((candidate) => candidate.ocr!.patterns.map((pattern) => pattern.languageTag.trim()))
      .filter((tag) => tag.length > 0),
  )].sort();

  const save = () => {
    const trimmedName = name.trim();
    if (!trimmedName) {
      setValidationError('Event name is required.');
      return;
    }
    if (detectionKind === 'Ocr' && ocrPatterns.some((pattern) =>
      !pattern.languageTag.trim() || !pattern.template.trim())) {
      setValidationError('Every OCR pattern requires a language and template.');
      return;
    }
    const parsedSubtractsEventId = subtractsEventId === '' ? null : Number(subtractsEventId);
    if (type === 'Subtractor' && (parsedSubtractsEventId === null || !Number.isInteger(parsedSubtractsEventId))) {
      setValidationError('A subtractor must reference a trigger event.');
      return;
    }
    if (type === 'Subtractor' && !events.some((candidate) =>
      candidate.id === parsedSubtractsEventId && candidate.type === 'Trigger' && candidate.id !== event.id)) {
      setValidationError('A subtractor must reference an existing trigger event.');
      return;
    }

    setValidationError(null);
    const objectClassId = detectionKind === 'Object' && event.detectionKind === 'Ocr'
      ? Math.max(-1, ...events
          .filter((candidate) => candidate.id !== event.id
            && (candidate.detectionKind ?? 'Object') === 'Object')
          .map((candidate) => candidate.classId)) + 1
      : event.classId;
    onSave({
      ...event,
      name: trimmedName,
      type,
      ...(detectionKind !== 'Object' || event.detectionKind !== undefined ? { detectionKind } : {}),
      ...(detectionKind === 'Ocr'
        ? {
            classId: -1,
            ocr: {
              ...event.ocr,
              patterns: ocrPatterns.map((pattern) => ({
                ...pattern,
                languageTag: pattern.languageTag.trim(),
                template: pattern.template.trim(),
              })),
            },
          }
        : {
            classId: objectClassId,
            ...(event.ocr !== undefined ? { ocr: null } : {}),
          }),
      bookmarkType: type === 'Subtractor' ? null : bookmarkType || null,
      includeInAutoClips: type === 'Trigger' && includeInAutoClips,
      ...(fixedPosition || event.fixedPosition !== undefined
        ? { fixedPosition: detectionKind === 'Object' && fixedPosition }
        : {}),
       ...(type === 'Subtractor' || event.subtractsEventId !== undefined
         ? { subtractsEventId: type === 'Subtractor' ? parsedSubtractsEventId : null }
         : {}),
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
          <Field label="Detection">
            <SelectField
              value={detectionKind}
              onChange={(value) => setDetectionKind(value as 'Object' | 'Ocr')}
              options={[{ value: 'Object', label: 'Object' }, { value: 'Ocr', label: 'OCR' }]}
            />
          </Field>
          <Field label="Type">
            <SelectField
              value={type}
              onChange={(value) => {
                const nextType = value as TrainingEventDefinition['type'];
                setType(nextType);
                if (nextType !== 'Subtractor') setSubtractsEventId('');
                if (nextType === 'Subtractor') setBookmarkType('');
              }}
              options={[{ value: 'Trigger', label: 'Trigger' }, { value: 'Exclusion', label: 'Exclusion' }, { value: 'Subtractor', label: 'Subtractor' }]}
            />
          </Field>
          <Field label="Bookmark">
            <SelectField
              value={bookmarkType}
              onChange={setBookmarkType}
              disabled={type === 'Subtractor'}
              options={[{ value: '', label: 'No bookmark' }, ...BOOKMARK_TYPES.map((value) => ({ value, label: value }))]}
            />
          </Field>
        </div>
        {detectionKind === 'Ocr' && (
          <div className="training-ocr-patterns">
            <div className="training-ocr-pattern-heading">
              <div>
                <strong>Text patterns</strong>
                <p>The recogniser reads the same text regardless of language; the tag just labels each wording. Any one pattern activating means this event fires. Wrap a run of words in braces, such as {'{name}'} or {'{name:1..4}'}, to match it as a variable.</p>
              </div>
              <Button
                variant="ghost"
                size="small"
                onClick={() => setOcrPatterns((current) => [...current, { languageTag: '', template: '' }])}
              >
                Add pattern
              </Button>
            </div>
            {ocrPatterns.map((pattern, index) => (
              <div className="training-ocr-pattern-row" key={index}>
                <div className="training-ocr-language">
                  <Field label={`Language ${index + 1}`}>
                    <TextField
                      value={pattern.languageTag}
                      placeholder="en-US"
                      onChange={(value) => updatePattern(index, { languageTag: value })}
                    />
                  </Field>
                  {knownLanguages.length > 0 && (
                    <SelectField
                      compact
                      aria-label={`Copy a language for pattern ${index + 1}`}
                      value=""
                       options={[
                         { value: '', label: 'Copy language…' },
                         ...knownLanguages.map((language) => ({ value: language, label: language })),
                       ]}
                      onChange={(value) => { if (value) updatePattern(index, { languageTag: value }); }}
                    />
                  )}
                </div>
                <Field label={`Pattern ${index + 1}`}>
                  <TextField
                    value={pattern.template}
                    placeholder="YOU DEFEATED {name:1..4}"
                    onChange={(value) => setOcrPatterns((current) => current.map((candidate, candidateIndex) =>
                      candidateIndex === index ? { ...candidate, template: value } : candidate))}
                  />
                </Field>
                <Button
                  variant="ghost"
                  size="small"
                  disabled={ocrPatterns.length === 1}
                  onClick={() => setOcrPatterns((current) => current.filter((_, candidateIndex) => candidateIndex !== index))}
                  aria-label={`Remove pattern ${index + 1}`}
                >
                  Remove
                </Button>
              </div>
            ))}
          </div>
        )}
        {type === 'Trigger' && (
          <label className="training-event-checkbox">
            <Checkbox checked={includeInAutoClips} onChange={setIncludeInAutoClips} />
            <span>Include in automatic clips</span>
          </label>
        )}
        {detectionKind === 'Object' && (
          <label className="training-event-checkbox">
            <Checkbox checked={fixedPosition} onChange={setFixedPosition} />
            <span>Fixed position and size across samples</span>
          </label>
        )}
        {type === 'Subtractor' && (
          <Field label="Subtracts event" hint="One matching trigger occurrence is removed per detected subtractor instance in the same batch.">
            <SelectField
              aria-label="Subtracts event"
              value={subtractsEventId}
              onChange={setSubtractsEventId}
              options={[
                { value: '', label: 'Choose trigger event' },
                ...events
                  .filter((candidate) => candidate.type === 'Trigger' && candidate.id !== event.id)
                  .map((candidate) => ({ value: String(candidate.id), label: candidate.name })),
              ]}
            />
          </Field>
        )}
        {validationError && <p className="training-event-error" role="alert">{validationError}</p>}
        <div className="training-event-dialog-actions">
          <Button variant="ghost" onClick={onCancel}>Cancel</Button>
          <Button onClick={save} disabled={!name.trim()}>Save event</Button>
        </div>
      </section>
    </div>
  );
}
