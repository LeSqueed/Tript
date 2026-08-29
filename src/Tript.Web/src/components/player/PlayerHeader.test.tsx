// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import { PlayerHeader } from './PlayerHeader';

const item = (contentType: ContentItem['contentType'], isHdr?: boolean): ContentItem => ({
  contentType,
  fileName: 'video.mp4',
  filePath: `${contentType}s/video.mp4`,
  isHdr,
});

function header(content: ContentItem, recording = false) {
  return <PlayerHeader
    item={content}
    creatingHighlights={false}
    highlightsPaused={false}
    highlightCount={0}
    onAutomaticClips={() => {}}
    convertHdrClipsToSdr
    recording={recording}
  />;
}

afterEach(cleanup);

describe('PlayerHeader SDR action', () => {
  it('shows the action for HDR clips and disables it while recording', () => {
    const view = render(header(item('clip', true)));
    expect((screen.getByRole('button', { name: 'Create SDR version' }) as HTMLButtonElement).disabled).toBe(false);

    view.rerender(header(item('clip', true), true));
    expect((screen.getByRole('button', { name: 'Create SDR version' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows the action for HDR highlights', () => {
    render(header(item('highlight', true)));
    expect(screen.getByRole('button', { name: 'Create SDR version' })).toBeTruthy();
  });

  it('hides the action for sessions and SDR clips', () => {
    const view = render(header(item('recording', true)));
    expect(screen.queryByRole('button', { name: 'Create SDR version' })).toBeNull();

    view.rerender(header(item('clip', false)));
    expect(screen.queryByRole('button', { name: 'Create SDR version' })).toBeNull();
  });
});

describe('PlayerHeader title actions', () => {
  it('renames the current item from the inline editor', () => {
    const onRename = vi.fn();
    const current = { ...item('recording'), title: 'Original title' };
    render(
      <PlayerHeader
        item={current}
        creatingHighlights={false}
        highlightsPaused={false}
        highlightCount={0}
        onAutomaticClips={() => {}}
        onRename={onRename}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Rename video' }));
    const input = screen.getByRole('textbox', { name: 'Video title' });
    fireEvent.change(input, { target: { value: 'Renamed video' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onRename).toHaveBeenCalledWith(current, 'Renamed video');
  });

  it('cancels a rename with Escape', () => {
    const onRename = vi.fn();
    render(
      <PlayerHeader
        item={item('clip')}
        creatingHighlights={false}
        highlightsPaused={false}
        highlightCount={0}
        onAutomaticClips={() => {}}
        onRename={onRename}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Rename video' }));
    const input = screen.getByRole('textbox', { name: 'Video title' });
    fireEvent.change(input, { target: { value: 'Not saved' } });
    fireEvent.keyDown(input, { key: 'Escape' });

    expect(onRename).not.toHaveBeenCalled();
    expect(screen.getByText('video.mp4')).toBeTruthy();
  });

  it('opens the current item in its native folder', () => {
    const onOpenFileLocation = vi.fn();
    const current = item('highlight');
    render(
      <PlayerHeader
        item={current}
        creatingHighlights={false}
        highlightsPaused={false}
        highlightCount={0}
        onAutomaticClips={() => {}}
        onOpenFileLocation={onOpenFileLocation}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Show video in folder' }));

    expect(onOpenFileLocation).toHaveBeenCalledWith(current);
  });
});
