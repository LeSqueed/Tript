// SPDX-License-Identifier: GPL-2.0-or-later
//
// Content-server URL construction by path. The frontend builds URLs by concatenation against the
// content server root; the backend resolves them against a canonical root (path-traversal guard on
// its side — this build keeps the frontend's side honest by normalising leading slashes).

import { describe, expect, it } from 'vitest';
import { contentUrl, thumbnailUrl } from './endpoints';

describe('contentUrl', () => {
  it('builds a content URL by path against the content server', () => {
    expect(contentUrl('session/recording-1.mp4')).toBe(
      'http://localhost:2222/api/content/session/recording-1.mp4',
    );
  });

  it('normalises leading slashes rather than building an absolute-looking path', () => {
    expect(contentUrl('/session/recording-1.mp4')).toBe(
      'http://localhost:2222/api/content/session/recording-1.mp4',
    );
  });

  it('builds thumbnail URLs on /api/thumbnail', () => {
    expect(thumbnailUrl('session/recording-1.jpg')).toBe(
      'http://localhost:2222/api/thumbnail/session/recording-1.jpg',
    );
  });
});
