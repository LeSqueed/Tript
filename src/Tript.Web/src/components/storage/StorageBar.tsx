// SPDX-License-Identifier: GPL-2.0-or-later

import type { StorageReportMessage } from '../../ipc/protocol';
import { formatStorageSize, storageSegments } from './storageModel';

export function StorageBar({
  report,
  compact = false,
  showLegend = true,
}: {
  report: StorageReportMessage;
  compact?: boolean;
  showLegend?: boolean;
}) {
  const segments = storageSegments(report).filter((segment) => segment.bytes > 0);
  const used = report.volumeTotalBytes > 0
    ? report.volumeTotalBytes - report.volumeFreeBytes
    : report.libraryBytes;

  return (
    <div className={compact ? 'storage-bar compact' : 'storage-bar'} data-testid="storage-bar">
      <div
        className="storage-bar-track"
        role="img"
        aria-label={`${formatStorageSize(used)} used, ${formatStorageSize(report.volumeFreeBytes)} free`}
      >
        {segments.map((segment) => (
          <span
            key={segment.kind}
            className={`storage-bar-segment storage-seg-${segment.kind}`}
            style={{ width: `${Math.max(segment.share * 100, segment.bytes > 0 ? 0.5 : 0)}%` }}
            title={`${segment.label}: ${formatStorageSize(segment.bytes)}`}
          />
        ))}
      </div>
      {showLegend && (
        <ul className="storage-legend">
          {segments.map((segment) => (
            <li key={segment.kind}>
              <span className={`storage-swatch storage-seg-${segment.kind}`} aria-hidden="true" />
              <span className="storage-legend-label">{segment.label}</span>
              <span className="storage-legend-value">{formatStorageSize(segment.bytes)}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
