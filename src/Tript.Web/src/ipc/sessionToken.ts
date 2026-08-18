// SPDX-License-Identifier: GPL-2.0-or-later
//
// The per-launch session token.
//
// The host mints a token once per launch and serves the UI only to a request carrying it, so the
// page arrives with `?k=<token>` in its own URL. Every listener (control socket, content server)
// requires the same token, and a browser cannot put a header on a WebSocket handshake or on a
// `<video src>` — the query string is the only channel either one has.
//
// The token lives in module scope for the life of the document and NOWHERE else: not in
// localStorage or sessionStorage, which outlive the launch that issued it, and not in the DOM as
// text, a log line or an error message.

/** The query-string parameter the host reads the token from. */
export const SESSION_TOKEN_PARAM = 'k';

/** The token carried by a query string, or null when it carries none. */
export function parseSessionToken(search: string): string | null {
  const value = new URLSearchParams(search).get(SESSION_TOKEN_PARAM);
  return value === null || value.length === 0 ? null : value;
}

let token: string | null = null;

/**
 * Read the token off the page URL. Runs once at startup (below); exported so a test can place a
 * token without navigating.
 */
export function captureSessionToken(search: string): void {
  token = parseSessionToken(search);
}

/** Whether this page was opened with a token. Callers must not read the token itself. */
export function hasSessionToken(): boolean {
  return token !== null;
}

/**
 * Append the token to an absolute URL, replacing any `k` already on it. Without a token the URL is
 * returned unchanged — an empty `k=` would be refused just the same and only muddies the request.
 */
export function withSessionToken(url: string): string {
  if (token === null) {
    return url;
  }
  const parsed = new URL(url);
  parsed.searchParams.set(SESSION_TOKEN_PARAM, token);
  return parsed.toString();
}

// At import, not at the call of some initialiser: nothing may build a URL before the token is known.
captureSessionToken(globalThis.location?.search ?? '');
