// SPDX-License-Identifier: GPL-2.0-or-later

import type { TrainingEventDefinition, TrainingSample } from '../../ipc/protocol';
import { Button, Field, SelectField, TextField } from '../ui/controls';
import type { SampleKindFilter } from './trainingMetrics';
import type { SamplePreviews } from './useSamplePreviews';

export function TrainingSampleList({
  totalCount,
  filteredCount,
  pageSamples,
  events,
  invalidById,
  previews,
  query,
  onQueryChange,
  kind,
  onKindChange,
  kindOptions,
  page,
  pageCount,
  onPageChange,
  onOpen,
}: {
  totalCount: number;
  filteredCount: number;
  pageSamples: TrainingSample[];
  events: TrainingEventDefinition[];
  invalidById: ReadonlyMap<string, string>;
  previews: SamplePreviews;
  query: string;
  onQueryChange: (query: string) => void;
  kind: SampleKindFilter;
  onKindChange: (kind: SampleKindFilter) => void;
  kindOptions: { value: SampleKindFilter; label: string }[];
  page: number;
  pageCount: number;
  onPageChange: (page: number) => void;
  onOpen: (sample: TrainingSample) => void;
}) {
  const eventName = (classId: number) => events.find((event) => event.classId === classId)?.name;

  return (
    <section className="panel training-panel training-samples-panel">
      <div className="training-panel-heading">
        <div>
          <p className="training-eyebrow">Samples</p>
          <h2>Captured frames</h2>
        </div>
        <span className="training-count">{filteredCount} of {totalCount} frames</span>
      </div>
      {totalCount === 0 ? (
        <div className="training-empty-state">
          <span className="training-empty-mark">01</span>
          <strong>No captured frames yet</strong>
          <p className="muted small">Open a recording, pause on the moment you want, then choose <strong>Label frame</strong>. The image and event palette open together.</p>
        </div>
      ) : (
        <>
          <div className="training-sample-toolbar">
            <Field label="Filter samples">
              <TextField
                value={query}
                onChange={onQueryChange}
                placeholder="Search labels, IDs, or timestamps"
                aria-label="Filter samples"
              />
            </Field>
            <Field label="Filter">
              <SelectField
                value={kind}
                onChange={(value) => onKindChange(value as SampleKindFilter)}
                aria-label="Filter samples by kind"
                options={kindOptions}
              />
            </Field>
            <span className="muted small">Showing {pageSamples.length} samples</span>
          </div>
          {pageSamples.length === 0 ? (
            <div className="training-empty-state">
              <span className="training-empty-mark">--</span>
              <strong>No samples match this filter</strong>
              <p className="muted small">Try an event name, sample ID, or timestamp.</p>
            </div>
          ) : (
            <div className="training-sample-list">
              {pageSamples.map((sample) => (
                <Button variant="ghost" className={`training-sample-card${invalidById.has(sample.id) ? ' invalid' : ''}`} key={sample.id} onClick={() => onOpen(sample)}>
                  {previews.images[sample.id] && !previews.failed[sample.id] ? (
                    <img
                      className="training-sample-thumb"
                      style={{ aspectRatio: `${sample.imageWidth} / ${sample.imageHeight}` }}
                      src={previews.images[sample.id]}
                      alt=""
                      onError={() => previews.retry(sample)}
                    />
                  ) : (
                    <span className="training-sample-thumb training-sample-thumb-empty">
                      {previews.failed[sample.id] ? 'Preview unavailable' : 'Loading preview'}
                    </span>
                  )}
                  <span className="training-sample-card-body">
                    <strong>{sample.id}</strong>
                    <small>{sample.labels.length} label{sample.labels.length === 1 ? '' : 's'} at {sample.timestampSeconds.toFixed(2)}s</small>
                    {invalidById.has(sample.id) && <span className="training-sample-invalid">Invalid: {invalidById.get(sample.id)}</span>}
                    <span className="training-label-chips">
                      {sample.labels.length === 0 ? (
                        <span className="training-label-chip muted">Unlabeled</span>
                      ) : sample.labels.map((label, index) => (
                        <span className="training-label-chip" key={`${sample.id}-${index}`}>
                          {eventName(label.classId) ?? `Class ${label.classId}`}
                        </span>
                      ))}
                    </span>
                  </span>
                </Button>
              ))}
            </div>
          )}
          <div className="training-pagination" aria-label="Sample pages">
            <Button variant="ghost" size="small" onClick={() => onPageChange(Math.max(1, page - 1))} disabled={page <= 1}>Previous</Button>
            <span className="muted small">Page {Math.min(page, pageCount)} of {pageCount}</span>
            <Button variant="ghost" size="small" onClick={() => onPageChange(Math.min(pageCount, page + 1))} disabled={page >= pageCount}>Next</Button>
          </div>
        </>
      )}
    </section>
  );
}
