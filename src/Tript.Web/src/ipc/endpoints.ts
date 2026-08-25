// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint constants for the Tript IPC surface, from the local-ipc contract.

import { withSessionToken } from './sessionToken';

/** WebSocket control socket — bidirectional command and state channel. */
export const CONTROL_SOCKET_URL = 'ws://localhost:8894/';

/** Content server root — /api/content (range-request video streaming), /api/thumbnail. */
export const CONTENT_SERVER_URL = 'http://localhost:8893/';

/** Protocol version carried on NewConnection; a frontend/backend mismatch must fail loudly. */
export const PROTOCOL_VERSION = 1;

/**
 * The control socket URL to connect to, carrying the per-launch token. A function rather than a
 * constant: the token is read at startup, and a constant would bake in whatever was known at import.
 */
export function controlSocketUrl(): string {
  return withSessionToken(CONTROL_SOCKET_URL);
}

/**
 * Build the URL for a piece of content served by the content server.
 * The path is relative to the content root and its segments are percent-encoded; the backend
 * resolves every request against a canonical root before serving (path-traversal guard on its side).
 * The per-launch token rides on the query string — the only channel a `<video src>` has.
 */
export function contentUrl(path: string): string {
  return withSessionToken(new URL(`api/content/${encodePath(path)}`, CONTENT_SERVER_URL).toString());
}

/** Build the URL for a thumbnail served by the content server. */
export function thumbnailUrl(path: string): string {
  return withSessionToken(new URL(`api/thumbnail/${encodePath(path)}`, CONTENT_SERVER_URL).toString());
}

// Encodes each segment and rejoins on '/'. Encoding the whole path would escape the separators too
// and address one long file name; leaving it raw lets a '#' in a file name truncate the request at
// the fragment (the server never sees the rest) and a '?' turn the tail into a query string.
function encodePath(path: string): string {
  return stripLeadingSlashes(path).split('/').map(encodeURIComponent).join('/');
}

function stripLeadingSlashes(path: string): string {
  return path.replace(/^\/+/, '');
}
