// SPDX-License-Identifier: GPL-2.0-or-later
//
// Telling "the host rejected my launch key" apart from "the host is not running".
//
// Both look identical on the control socket. A handshake refused with 403 and a connection refused
// outright reach script the same way — an `error` event followed by a close with code 1006 — because
// browsers deliberately hide a failed WebSocket handshake's HTTP status from the page. So the socket
// cannot answer the question and nothing it reports should be read as if it could.
//
// The UI host can. It served this document, so it is same-origin, and a same-origin `fetch` may read
// the status: 403 is the host answering that it does not accept this page's key (it minted a new one
// when it restarted), any other answer means the key is still good, and a fetch that fails at the
// transport means nothing is listening on that port at all.

import { withSessionToken } from './sessionToken';

/** What a probe of the UI host found. */
export type HostReachability =
  /** The host answered and still accepts this page's key — whatever is wrong, it is not the key. */
  | 'accepted'
  /** The host answered 403: it is running, and this page's key is not one it accepts. */
  | 'rejected'
  /** Nothing answered on the UI host's port. */
  | 'unreachable';

/** The status the host refuses an unaccepted key with (UiHost.Refuse). */
export const REJECTED_STATUS = 403;

/** A status the host actually answered with, classified. Pure — the whole decision lives here. */
export function classifyProbeStatus(status: number): HostReachability {
  return status === REJECTED_STATUS ? 'rejected' : 'accepted';
}

/** The URL to probe: the host's own root, carrying this page's key. */
export function hostProbeUrl(origin: string): string {
  return withSessionToken(new URL('/', origin).toString());
}

export interface HostProbeOptions {
  fetchImpl?: typeof globalThis.fetch;
  origin?: string;
}

/**
 * Ask the UI host whether it still accepts this page's key. Never throws: a transport failure is an
 * answer ('unreachable'), not an error to handle elsewhere.
 */
export async function probeHost(options: HostProbeOptions = {}): Promise<HostReachability> {
  const {
    fetchImpl = globalThis.fetch?.bind(globalThis),
    origin = globalThis.location?.origin ?? '',
  } = options;
  try {
    // `no-store` so a cached index.html cannot answer for a host that is gone, and `manual` so a
    // redirect is reported as itself rather than followed somewhere unrelated.
    const response = await fetchImpl(hostProbeUrl(origin), { cache: 'no-store', redirect: 'manual' });
    return classifyProbeStatus(response.status);
  } catch {
    return 'unreachable';
  }
}
