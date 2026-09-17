// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import type { TrainingEventDefinition, TrainingSample } from '../../ipc/protocol';
import { filterTrainingSamples, formatDuration, recordEpoch, trainingPace } from './trainingMetrics';

describe('formatDuration', () => {
  it('shows seconds alone under a minute and pads them after', () => {
    expect(formatDuration(0)).toBe('0s');
    expect(formatDuration(42_400)).toBe('42s');
    expect(formatDuration(65_000)).toBe('1m 05s');
    expect(formatDuration(-5)).toBe('0s');
  });
});

describe('trainingPace', () => {
  it('needs two epochs before it can estimate', () => {
    expect(trainingPace([{ epoch: 1, loss: 1, map50: null, receivedAt: 0 }], 10))
      .toEqual({ elapsedMs: 0, remainingMs: null });
  });

  it('uses the median epoch time, so one slow epoch does not skew it', () => {
    const history = [0, 1000, 2000, 9000, 10_000].map((receivedAt, index) => ({
      epoch: index + 1, loss: null, map50: null, receivedAt,
    }));

    expect(trainingPace(history, 10)).toEqual({ elapsedMs: 5000, remainingMs: 5000 });
  });
});

describe('recordEpoch', () => {
  it('replaces a repeated epoch and keeps the history ordered', () => {
    const history = recordEpoch(
      [{ epoch: 2, loss: 0.5, map50: null, receivedAt: 2 }, { epoch: 1, loss: 0.9, map50: null, receivedAt: 1 }],
      { epoch: 2, loss: 0.4, map50: null, receivedAt: 3 },
    );

    expect(history.map((point) => [point.epoch, point.loss])).toEqual([[1, 0.9], [2, 0.4]]);
  });
});

describe('filterTrainingSamples', () => {
  const events = [{ id: 1, classId: 0, name: 'Kill' }] as TrainingEventDefinition[];
  const samples = [
    { id: 'a', timestampSeconds: 1, labels: [{ classId: 0 }], ocrRegions: [] },
    { id: 'b', timestampSeconds: 2.5, labels: [], ocrRegions: [{ text: 'VICTORY' }] },
    { id: 'c', timestampSeconds: 3, labels: [], ocrRegions: [] },
  ] as unknown as TrainingSample[];
  const invalid = new Map([['c', 'empty']]);
  const ids = (list: TrainingSample[]) => list.map((sample) => sample.id);

  it('filters by kind', () => {
    expect(ids(filterTrainingSamples(samples, events, invalid, 'all', ''))).toEqual(['a', 'b', 'c']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'invalid', ''))).toEqual(['c']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'valid', ''))).toEqual(['a', 'b']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'ocr', ''))).toEqual(['b']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'object', ''))).toEqual(['a']);
  });

  it('matches event names, OCR text and timestamps without regard to case', () => {
    expect(ids(filterTrainingSamples(samples, events, invalid, 'all', ' kill '))).toEqual(['a']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'all', 'victory'))).toEqual(['b']);
    expect(ids(filterTrainingSamples(samples, events, invalid, 'all', '2.50'))).toEqual(['b']);
  });
});
