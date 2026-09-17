// SPDX-License-Identifier: GPL-2.0-or-later

import type { TrainingEventDefinition, TrainingSample } from '../../ipc/protocol';

export interface TrainingEpochPoint {
  epoch: number;
  loss: number | null;
  map50: number | null;
  receivedAt: number;
}

export type SampleKindFilter = 'all' | 'invalid' | 'valid' | 'ocr' | 'object';

const PACE_WINDOW = 7;

export function formatDuration(totalMs: number): string {
  const totalSeconds = Math.max(0, Math.round(totalMs / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes === 0 ? `${seconds}s` : `${minutes}m ${String(seconds).padStart(2, '0')}s`;
}

export function trainingPace(history: TrainingEpochPoint[], totalEpochs: number): { elapsedMs: number; remainingMs: number | null } {
  if (history.length < 2) return { elapsedMs: 0, remainingMs: null };
  const last = history[history.length - 1];
  const intervals = history.slice(1).map((point, index) => {
    const previous = history[index];
    return Math.max(0, point.receivedAt - previous.receivedAt) / Math.max(1, point.epoch - previous.epoch);
  }).slice(-PACE_WINDOW).sort((left, right) => left - right);
  const middle = Math.floor(intervals.length / 2);
  const perEpochMs = intervals.length % 2 === 0
    ? (intervals[middle - 1] + intervals[middle]) / 2
    : intervals[middle];
  return {
    elapsedMs: perEpochMs * last.epoch,
    remainingMs: perEpochMs * Math.max(0, totalEpochs - last.epoch),
  };
}

export function recordEpoch(history: TrainingEpochPoint[], point: TrainingEpochPoint): TrainingEpochPoint[] {
  return [...history.filter((entry) => entry.epoch !== point.epoch), point]
    .sort((a, b) => a.epoch - b.epoch);
}

export function filterTrainingSamples(
  samples: TrainingSample[],
  events: TrainingEventDefinition[],
  invalidById: ReadonlyMap<string, string>,
  kind: SampleKindFilter,
  query: string,
): TrainingSample[] {
  const normalized = query.trim().toLowerCase();
  return samples.filter((sample) => {
    const isInvalid = invalidById.has(sample.id);
    if (kind === 'invalid' && !isInvalid) return false;
    if (kind === 'valid' && isInvalid) return false;
    if (kind === 'ocr' && (sample.ocrRegions?.length ?? 0) === 0) return false;
    if (kind === 'object' && sample.labels.length === 0) return false;
    if (!normalized) return true;
    const labelNames = sample.labels.map((label) => events.find((event) => event.classId === label.classId)?.name ?? String(label.classId));
    const ocrText = (sample.ocrRegions ?? []).map((region) => region.text);
    return [sample.id, sample.timestampSeconds.toFixed(2), ...labelNames, ...ocrText]
      .some((value) => value.toLowerCase().includes(normalized));
  });
}
