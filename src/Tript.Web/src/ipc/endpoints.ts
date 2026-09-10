// SPDX-License-Identifier: GPL-2.0-or-later

import { withSessionToken } from './sessionToken';

export const CONTROL_SOCKET_URL = 'ws://localhost:8894/';

export const CONTENT_SERVER_URL = 'http://localhost:8893/';

export const PROTOCOL_VERSION = 1;

export function controlSocketUrl(): string {
  return withSessionToken(CONTROL_SOCKET_URL);
}

export function contentUrl(path: string): string {
  return withSessionToken(new URL(`api/content/${encodePath(path)}`, CONTENT_SERVER_URL).toString());
}

export function thumbnailUrl(path: string): string {
  return withSessionToken(new URL(`api/thumbnail/${encodePath(path)}`, CONTENT_SERVER_URL).toString());
}

function encodePath(path: string): string {
  return stripLeadingSlashes(path).split('/').map(encodeURIComponent).join('/');
}

function stripLeadingSlashes(path: string): string {
  return path.replace(/^\/+/, '');
}
