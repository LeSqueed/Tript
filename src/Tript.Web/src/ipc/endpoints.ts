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
 * Paths are constructed by concatenation; the backend resolves every request against a
 * canonical root before serving (path-traversal guard on its side).
 */
export function contentUrl(path: string): string {
  return new URL(`api/content/${stripLeadingSlashes(path)}`, CONTENT_SERVER_URL).toString();
}

/** Build the URL for a thumbnail served by the content server. */
export function thumbnailUrl(path: string): string {
  return new URL(`api/thumbnail/${stripLeadingSlashes(path)}`, CONTENT_SERVER_URL).toString();
}

function stripLeadingSlashes(path: string): string {
  return path.replace(/^\/+/, '');
}
