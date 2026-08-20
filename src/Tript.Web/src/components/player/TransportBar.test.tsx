// SPDX-License-Identifier: GPL-2.0-or-later
//
// The transport bar is the player's whole control surface now that the video no longer carries the
// browser's own controls, so volume and mute are covered here rather than assumed from the browser.

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TransportBar, type TransportBarProps } from './TransportBar';

afterEach(cleanup);

function renderBar(overrides: Partial<TransportBarProps> = {}) {
  const props: TransportBarProps = {
    playing: false,
    currentTime: 0,
    duration: 100,
    volume: 1,
    muted: false,
    onTogglePlayPause: vi.fn(),
    onToggleFullscreen: vi.fn(),
    onVolumeChange: vi.fn(),
    onToggleMute: vi.fn(),
    ...overrides,
  };
  render(<TransportBar {...props} />);
  return props;
}

describe('TransportBar', () => {
  it('reports a new volume when the slider moves', () => {
    const props = renderBar();
    fireEvent.change(screen.getByLabelText('Volume'), { target: { value: '0.4' } });
    expect(props.onVolumeChange).toHaveBeenCalledWith(0.4);
  });

  it('toggles mute and names the action for what it will do', () => {
    const props = renderBar();
    fireEvent.click(screen.getByRole('button', { name: 'Mute' }));
    expect(props.onToggleMute).toHaveBeenCalled();
  });

  it('offers to unmute while muted', () => {
    renderBar({ muted: true });
    expect(screen.getByRole('button', { name: 'Unmute' })).toBeTruthy();
  });

  it('shows the slider at zero while muted', () => {
    renderBar({ muted: true, volume: 0.8 });
    expect((screen.getByLabelText('Volume') as HTMLInputElement).value).toBe('0');
  });

  it('still carries play/pause and the time readout', () => {
    renderBar({ playing: true, currentTime: 65, duration: 100 });
    expect(screen.getByRole('button', { name: 'Play or pause' }).textContent).toBe('Pause');
    expect(screen.getByTestId('transport-current').textContent).toBe('1:05');
    expect(screen.getByTestId('transport-duration').textContent).toBe('1:40');
  });
});
