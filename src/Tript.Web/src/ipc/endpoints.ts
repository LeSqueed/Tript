// SPDX-License-Identifier: GPL-2.0-or-later
//
// Endpoint constants for the Tript IPC surface, from the local-ipc contract.

/** WebSocket control socket — bidirectional command and state channel. */
export const CONTROL_SOCKET_URL = 'ws://localhost:44030/';

/** Content server root — /api/content (range-request video streaming), /api/thumbnail. */
export const CONTENT_SERVER_URL = 'http://localhost:2222/';

/** Protocol version carried on NewConnection; a frontend/backend mismatch must fail loudly. */
export const PROTOCOL_VERSION = 1;

/**
 * Build the URL for a piece of content served by the content server.
 * The path is relative to the content root and its segments are percent-encoded; the backend
 * resolves every request against a canonical root before serving (path-traversal guard on its side).
 */
export function contentUrl(path: string): string {
  return new URL(`api/content/${encodePath(path)}`, CONTENT_SERVER_URL).toString();
}

/** Build the URL for a thumbnail served by the content server. */
export function thumbnailUrl(path: string): string {
  return new URL(`api/thumbnail/${encodePath(path)}`, CONTENT_SERVER_URL).toString();
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
