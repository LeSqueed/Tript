import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { LoadingOverlay } from './LoadingOverlay';

describe('LoadingOverlay', () => {
  afterEach(() => {
    cleanup();
    vi.useRealTimers();
  });

  it('waits for the delay before appearing to avoid flicker', () => {
    vi.useFakeTimers();
    render(<LoadingOverlay title="Working" description="Please wait" />);
    expect(screen.queryByRole('dialog', { name: 'Working' })).toBeNull();
    act(() => vi.advanceTimersByTime(250));
    expect(screen.getByRole('dialog', { name: 'Working' })).toBeTruthy();
    expect(screen.getByText('Please wait')).toBeTruthy();
  });

  it('renders a progress bar with the given percentage', () => {
    vi.useFakeTimers();
    render(<LoadingOverlay title="Working" progress={42} />);
    act(() => vi.advanceTimersByTime(250));
    const bar = screen.getByRole('progressbar');
    expect(bar.getAttribute('aria-valuenow')).toBe('42');
  });

  it('renders a cancel action and invokes the handler when provided', () => {
    vi.useFakeTimers();
    const onCancel = vi.fn();
    render(<LoadingOverlay title="Working" cancelLabel="Cancel" onCancel={onCancel} />);
    act(() => vi.advanceTimersByTime(250));

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('omits the cancel action without an onCancel handler', () => {
    vi.useFakeTimers();
    render(<LoadingOverlay title="Working" cancelLabel="Cancel" />);
    act(() => vi.advanceTimersByTime(250));
    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull();
  });
});
