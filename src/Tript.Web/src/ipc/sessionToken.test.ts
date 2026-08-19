// SPDX-License-Identifier: GPL-2.0-or-later
//
// The per-launch session token: parsed off the page's own query string, held in memory, appended to
// every URL that leaves the page. The interesting cases are the absent token (the UI must be able to
// tell), a token that must survive an already percent-encoded path, and the storage rule — a token
// written to localStorage would outlive the launch it belongs to.

import { afterEach, describe, expect, it } from 'vitest';
import {
  captureSessionToken,
  hasSessionToken,
  parseSessionToken,
  withSessionToken,
} from './sessionToken';

const TOKEN = 'b3f1c0de4a5b6c7d8e9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f';

afterEach(() => {
  captureSessionToken('');
});

describe('parseSessionToken', () => {
  it('reads the token the host put on the page URL', () => {
    expect(parseSessionToken(`?k=${TOKEN}`)).toBe(TOKEN);
  });

  it('reads it alongside other parameters, in any position', () => {
    expect(parseSessionToken(`?debug=1&k=${TOKEN}&x=2`)).toBe(TOKEN);
  });

  it('is null when the page carried no query string at all', () => {
    expect(parseSessionToken('')).toBeNull();
  });

  it('is null for an empty token rather than reporting one it does not have', () => {
    expect(parseSessionToken('?k=')).toBeNull();
  });

  it('is null when some other parameter is present but the token is not', () => {
    expect(parseSessionToken('?route=library')).toBeNull();
  });
});

describe('the captured token', () => {
  it('reports a token once the page URL has been read', () => {
    captureSessionToken(`?k=${TOKEN}`);
    expect(hasSessionToken()).toBe(true);
  });

  it('reports none when the page was opened without one', () => {
    captureSessionToken('?route=library');
    expect(hasSessionToken()).toBe(false);
  });

  // The token dies with the launch that issued it; storage does not. A stale token in storage is a
  // UI that looks connected and is refused by every listener.
  it('is not written to localStorage or sessionStorage', () => {
    captureSessionToken(`?k=${TOKEN}`);

    expect(window.localStorage?.length ?? 0).toBe(0);
    expect(window.sessionStorage?.length ?? 0).toBe(0);
  });
});

describe('withSessionToken', () => {
  it('appends the token as a query parameter', () => {
    captureSessionToken(`?k=${TOKEN}`);
    expect(withSessionToken('http://localhost:2222/api/content/a.mp4')).toBe(
      `http://localhost:2222/api/content/a.mp4?k=${TOKEN}`,
    );
  });

  it('leaves an already percent-encoded path untouched', () => {
    captureSessionToken(`?k=${TOKEN}`);
    const url = withSessionToken('http://localhost:2222/api/content/my%23clip.mp4');

    expect(new URL(url).pathname).toBe('/api/content/my%23clip.mp4');
    expect(new URL(url).searchParams.get('k')).toBe(TOKEN);
  });

  it('carries the token on a WebSocket URL, the only channel a handshake has', () => {
    captureSessionToken(`?k=${TOKEN}`);
    expect(withSessionToken('ws://localhost:44030/')).toBe(`ws://localhost:44030/?k=${TOKEN}`);
  });

  it('keeps existing query parameters and does not duplicate the token', () => {
    captureSessionToken(`?k=${TOKEN}`);
    const url = withSessionToken(`http://localhost:2222/api/content/a.mp4?t=3&k=stale`);

    expect(new URL(url).searchParams.getAll('k')).toEqual([TOKEN]);
    expect(new URL(url).searchParams.get('t')).toBe('3');
  });

  it('returns the URL unchanged when there is no token, rather than sending an empty one', () => {
    captureSessionToken('');
    expect(withSessionToken('http://localhost:2222/api/content/a.mp4')).toBe(
      'http://localhost:2222/api/content/a.mp4',
    );
  });
});
