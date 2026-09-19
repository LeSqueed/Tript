// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { StorageFreeChip } from './StorageFreeChip';
import type { StorageStatusMessage } from '../../ipc/protocol';

const GIGABYTE = 1024 * 1024 * 1024;

function status(patch: Partial<StorageStatusMessage> = {}): StorageStatusMessage {
  return {
    pressure: 'ok',
    freeBytes: 128 * GIGABYTE,
    totalBytes: 1024 * GIGABYTE,
    minimumFreeBytes: 20 * GIGABYTE,
    warnFreeBytes: 60 * GIGABYTE,
    recordingBlocked: false,
    policyConfirmed: true,
    whenFull: 'PauseRecording',
    keepSharingWhenFull: true,
    volumeRoot: 'T:\\',
    root: 'T:\\Tript',
    scratchFreeBytes: 0,
    scratchLow: false,
    ...patch,
  };
}

function chip(): HTMLElement | null {
  return screen.queryByTestId('storage-free-chip');
}

function dot(): Element | null {
  return chip()?.querySelector('.status-dot') ?? null;
}

afterEach(() => {
  cleanup();
});

describe('StorageFreeChip', () => {
  it('stays out of the way until a status has arrived', () => {
    render(<StorageFreeChip status={null} onOpenStorageSettings={vi.fn()} />);
    expect(chip()).toBeNull();
  });

  it('says nothing when the drive could not be read', () => {
    render(<StorageFreeChip status={status({ pressure: 'unknown' })} onOpenStorageSettings={vi.fn()} />);
    expect(chip()).toBeNull();
  });

  it('reports how much room is left', () => {
    render(<StorageFreeChip status={status()} onOpenStorageSettings={vi.fn()} />);
    expect(chip()?.textContent).toContain('128 GB free');
  });

  it('leaves the drive size out of the library', () => {
    render(<StorageFreeChip status={status()} onOpenStorageSettings={vi.fn()} />);
    expect(chip()?.textContent).not.toContain('1 TB');
  });

  it('carries the pressure so it can colour itself', () => {
    render(<StorageFreeChip status={status({ pressure: 'warning' })} onOpenStorageSettings={vi.fn()} />);
    expect(chip()?.getAttribute('data-pressure')).toBe('warning');
  });

  it('turns the dot green, amber and red as the drive fills', () => {
    render(<StorageFreeChip status={status()} onOpenStorageSettings={vi.fn()} />);
    expect(dot()?.className).toContain('status-dot-success');
    cleanup();

    render(<StorageFreeChip status={status({ pressure: 'warning' })} onOpenStorageSettings={vi.fn()} />);
    expect(dot()?.className).toContain('status-dot-warning');
    cleanup();

    render(<StorageFreeChip status={status({ pressure: 'critical' })} onOpenStorageSettings={vi.fn()} />);
    expect(dot()?.className).toContain('status-dot-error');
  });

  it('opens the storage settings when clicked', () => {
    const onOpen = vi.fn();
    render(<StorageFreeChip status={status()} onOpenStorageSettings={onOpen} />);
    fireEvent.click(chip()!);
    expect(onOpen).toHaveBeenCalled();
  });
});
