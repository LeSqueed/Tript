// SPDX-License-Identifier: GPL-2.0-or-later

import { withSessionToken } from './sessionToken';

export type HostReachability =
  | 'accepted'
  | 'rejected'
  | 'unreachable';

export const REJECTED_STATUS = 403;

export function classifyProbeStatus(status: number): HostReachability {
  return status === REJECTED_STATUS ? 'rejected' : 'accepted';
}

export function hostProbeUrl(origin: string): string {
  return withSessionToken(new URL('/', origin).toString());
}

export interface HostProbeOptions {
  fetchImpl?: typeof globalThis.fetch;
  origin?: string;
}

export async function probeHost(options: HostProbeOptions = {}): Promise<HostReachability> {
  const {
    fetchImpl = globalThis.fetch?.bind(globalThis),
    origin = globalThis.location?.origin ?? '',
  } = options;
  try {
    const response = await fetchImpl(hostProbeUrl(origin), { cache: 'no-store', redirect: 'manual' });
    return classifyProbeStatus(response.status);
  } catch {
    return 'unreachable';
  }
}
