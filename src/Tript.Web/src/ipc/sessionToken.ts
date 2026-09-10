// SPDX-License-Identifier: GPL-2.0-or-later

export const SESSION_TOKEN_PARAM = 'k';

export function parseSessionToken(search: string): string | null {
  const value = new URLSearchParams(search).get(SESSION_TOKEN_PARAM);
  return value === null || value.length === 0 ? null : value;
}

let token: string | null = null;

export function captureSessionToken(search: string): void {
  token = parseSessionToken(search);
}

export function hasSessionToken(): boolean {
  return token !== null;
}

export function withSessionToken(url: string): string {
  if (token === null) {
    return url;
  }
  const parsed = new URL(url);
  parsed.searchParams.set(SESSION_TOKEN_PARAM, token);
  return parsed.toString();
}

captureSessionToken(globalThis.location?.search ?? '');
