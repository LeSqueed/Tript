// SPDX-License-Identifier: GPL-2.0-or-later

import type { TrainingProgressMessage } from '../../ipc/protocol';
import { Button, Field, SelectField, TextField } from '../ui/controls';
import { TrainingSparkline } from './TrainingSparkline';
import { formatDuration, trainingPace, type TrainingEpochPoint } from './trainingMetrics';

export type TrainingScope = 'all' | 'object' | 'ocr';

const DEVICE_OPTIONS = [
  { value: 'auto', label: 'Auto GPU / CPU' },
  { value: 'rocm', label: 'ROCm / AMD GPU' },
  { value: 'cuda', label: 'CUDA GPU' },
  { value: 'directml', label: 'DirectML / AMD GPU' },
  { value: 'cpu', label: 'CPU' },
];

const AUGMENT_OPTIONS = [
  { value: '0', label: 'Off' },
  { value: '2', label: '2 copies per crop' },
  { value: '4', label: '4 copies per crop' },
  { value: '8', label: '8 copies per crop' },
];

const SCOPE_OPTIONS = [
  { value: 'all', label: 'Object + OCR' },
  { value: 'object', label: 'Object detection only' },
  { value: 'ocr', label: 'OCR recogniser only' },
];

const DEFAULT_INPUT_SIZE = 640;

export interface TrainingOptions {
  epochs: number;
  device: string;
  augmentCopies: number;
  ocrEpochs: number;
  scope: TrainingScope;
}

export function TrainingControlsPanel({
  options,
  onOptionsChange,
  hasObjectEvents,
  hasOcrEvents,
  invalidCount,
  validLabeledCount,
  active,
  epochDetails,
  epochHistory,
  statusNote,
  modelSize,
  onStart,
  onCancel,
}: {
  options: TrainingOptions;
  onOptionsChange: (patch: Partial<TrainingOptions>) => void;
  hasObjectEvents: boolean;
  hasOcrEvents: boolean;
  invalidCount: number;
  validLabeledCount: number;
  active: boolean;
  epochDetails: NonNullable<TrainingProgressMessage['details']> | null;
  epochHistory: TrainingEpochPoint[];
  statusNote: string | null;
  modelSize: { width?: number | null; height?: number | null };
  onStart: () => void;
  onCancel: () => void;
}) {
  const canPickScope = hasObjectEvents && hasOcrEvents;
  const effectiveScope = canPickScope ? options.scope : 'all';
  const willTrainObject = hasObjectEvents && effectiveScope !== 'ocr';
  const displayEpochs = willTrainObject ? options.epochs : options.ocrEpochs;
  const displayDevice = willTrainObject ? options.device : 'cpu';
  const totalEpochs = epochDetails?.epochs ?? displayEpochs;
  const pace = trainingPace(epochHistory, totalEpochs);
  const hasLoss = epochHistory.some((point) => point.loss != null);
  const hasMap50 = epochHistory.some((point) => point.map50 != null);
  const accuracyLabel = willTrainObject ? 'mAP50' : 'exact match';

  return (
    <section className="panel training-panel training-controls">
      <div>
        <p className="training-eyebrow">Train</p>
        <h2>Build and activate</h2>
        <p className="muted">A model can only be trained from labeled frames. It is validated against the event contract before reload.</p>
        {invalidCount > 0 && (
          <p className="training-invalid-warning" role="alert">
            {invalidCount} invalid sample{invalidCount === 1 ? '' : 's'} will be skipped. {validLabeledCount} valid labeled sample{validLabeledCount === 1 ? '' : 's'} available.
          </p>
        )}
      </div>
      {active && (
        <div className="training-active-status" role="status">
          <div className="training-active-heading">
            <span className="training-active-dot" aria-hidden="true" />
            <strong>Training in progress</strong>
            {epochDetails && (
              <span className="training-epoch-badge">Epoch {epochDetails.epoch}/{epochDetails.epochs}</span>
            )}
          </div>
          <span className="muted small">
            Requested device: {displayDevice === 'auto' ? 'Auto (actual device is shown in the console)' : displayDevice.toUpperCase()}
            {' · '} {displayEpochs} epochs
            {willTrainObject && ` · ${modelSize.width ?? DEFAULT_INPUT_SIZE}x${modelSize.height ?? DEFAULT_INPUT_SIZE} input`}
          </span>
          {epochDetails && (
            <div
              className="training-epoch-progress"
              role="progressbar"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={Math.min(100, Math.round((epochDetails.epoch / epochDetails.epochs) * 100))}
            >
              <span style={{ width: `${Math.min(100, (epochDetails.epoch / epochDetails.epochs) * 100)}%` }} />
            </div>
          )}
          {(hasLoss || hasMap50) && (
            <div className="training-metrics">
              <TrainingSparkline points={epochHistory} totalEpochs={totalEpochs} />
              <span className="muted small training-metrics-legend">
                {hasLoss && <span className="training-metrics-legend-loss">loss</span>}
                {hasMap50 && <span className="training-metrics-legend-map">{accuracyLabel}</span>}
              </span>
            </div>
          )}
          {epochDetails && (
            <span className="muted small training-metrics-numbers">
              {epochDetails.loss != null && <span>loss {epochDetails.loss.toFixed(4)}</span>}
              {epochDetails.map50 != null && <span>{accuracyLabel} {epochDetails.map50.toFixed(4)}</span>}
              {pace.elapsedMs > 0 && <span>{formatDuration(pace.elapsedMs)} elapsed</span>}
              {pace.remainingMs != null && <span>~{formatDuration(pace.remainingMs)} remaining</span>}
            </span>
          )}
          {statusNote && <span className="muted small">{statusNote}</span>}
          <span className="muted small">Detailed training output is in the console window.</span>
        </div>
      )}
      {hasObjectEvents && (
        <>
          <div className="training-field compact">
            <Field label={hasOcrEvents ? 'Object epochs' : 'Epochs'}>
              <TextField type="number" value={options.epochs} min={1} onChange={(value) => onOptionsChange({ epochs: Number(value) })} />
            </Field>
          </div>
          <div className="training-field compact">
            <Field label={hasOcrEvents ? 'Object device' : 'Device'}>
              <SelectField value={options.device} onChange={(device) => onOptionsChange({ device })} options={DEVICE_OPTIONS} />
            </Field>
          </div>
          <div className="training-field compact">
            <Field label="Augmentation" hint="Adds mildly distorted copies of each training crop to grow small sample sets. Validation is never augmented.">
              <SelectField
                value={String(options.augmentCopies)}
                onChange={(value) => onOptionsChange({ augmentCopies: Number(value) })}
                options={AUGMENT_OPTIONS}
              />
            </Field>
          </div>
        </>
      )}
      {hasOcrEvents && (
        <div className="training-field compact">
          <Field label={hasObjectEvents ? 'OCR epochs' : 'Epochs'}
            hint="The OCR recogniser fine-tunes from the pretrained PP-OCRv4 model on CPU.">
            <TextField type="number" value={options.ocrEpochs} min={1} onChange={(value) => onOptionsChange({ ocrEpochs: Number(value) })} />
          </Field>
        </div>
      )}
      {canPickScope && (
        <div className="training-field compact">
          <Field label="Retrain" hint="This game has object and OCR events. Retrain just one; the other model is kept as installed.">
            <SelectField
              value={options.scope}
              onChange={(value) => onOptionsChange({ scope: value as TrainingScope })}
              options={SCOPE_OPTIONS}
            />
          </Field>
        </div>
      )}
      <Button onClick={onStart} disabled={active || validLabeledCount === 0}>
        Start training
      </Button>
      <Button variant="ghost" onClick={onCancel} disabled={!active}>
        Cancel
      </Button>
    </section>
  );
}
