// SPDX-License-Identifier: GPL-2.0-or-later

import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
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
