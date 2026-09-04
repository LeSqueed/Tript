import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
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
});
