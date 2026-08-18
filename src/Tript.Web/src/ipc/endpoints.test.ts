// SPDX-License-Identifier: GPL-2.0-or-later
//
// Content-server URL construction by path. The frontend builds URLs against the content server
// root; the backend resolves them against a canonical root (path-traversal guard on its side —
// this build keeps the frontend's side honest by normalising leading slashes and encoding the
// segments).

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

describe('content URL encoding', () => {
  // A '#' in a file name is the worst case: unencoded it starts the fragment, the server is asked
  // for the truncated path and answers about a different file entirely.
  it('encodes a hash in a file name rather than starting a fragment', () => {
    const url = contentUrl('session/my#clip.mp4');

    expect(url).toBe('http://localhost:2222/api/content/session/my%23clip.mp4');
    expect(new URL(url).hash).toBe('');
    expect(new URL(url).pathname).toBe('/api/content/session/my%23clip.mp4');
  });

  it('encodes a question mark rather than starting a query string', () => {
    const url = contentUrl('session/is it?.mp4');

    expect(new URL(url).search).toBe('');
    expect(url).toBe('http://localhost:2222/api/content/session/is%20it%3F.mp4');
  });

  it('encodes a percent sign so it cannot read as an escape', () => {
    expect(contentUrl('session/100%.mp4')).toBe('http://localhost:2222/api/content/session/100%25.mp4');
  });

  it('keeps the path separators as separators while encoding the segments', () => {
    expect(contentUrl('a b/c#d/e.mp4')).toBe('http://localhost:2222/api/content/a%20b/c%23d/e.mp4');
  });

  it('encodes thumbnail paths the same way', () => {
    const url = thumbnailUrl('session/my#clip.jpg');

    expect(url).toBe('http://localhost:2222/api/thumbnail/session/my%23clip.jpg');
    expect(new URL(url).hash).toBe('');
  });
});
