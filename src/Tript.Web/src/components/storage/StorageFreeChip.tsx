// SPDX-License-Identifier: GPL-2.0-or-later

import type { StoragePressure, StorageStatusMessage } from '../../ipc/protocol';
import { StatusDot } from '../ui/Ui';
import { formatStorageSize } from './storageModel';

const TONES: Record<StoragePressure, 'success' | 'warning' | 'error' | 'neutral'> = {
  unknown: 'neutral',
  ok: 'success',
  warning: 'warning',
  critical: 'error',
};

export function StorageFreeChip({
  status,
  onOpenStorageSettings,
}: {
  status: StorageStatusMessage | null;
  onOpenStorageSettings: () => void;
}) {
  if (!status || status.pressure === 'unknown') {
    return null;
  }

  return (
    <button
      type="button"
      className="storage-free-chip"
      data-testid="storage-free-chip"
      data-pressure={status.pressure}
      title="Open storage settings"
      onClick={onOpenStorageSettings}
    >
      <StatusDot tone={TONES[status.pressure]} />
      <span>{formatStorageSize(status.freeBytes)} free</span>
    </button>
  );
}
