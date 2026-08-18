// SPDX-License-Identifier: GPL-2.0-or-later
//
// What to say when the control socket is down, given what the UI host answered about this page's
// launch key (ipc/hostProbe.ts).
//
// The remedy for a rejected key is NOT "reload". The key is in this page's own URL, so a reload
// presents the same stale key and the host answers the plain-text refusal instead of the app — a
// worse place to be than this banner. What recovers is the address the running host printed, which
// carries the key it minted this time.

import type { HostReachability } from '../ipc/hostProbe';

export interface ConnectionNotice {
  tone: 'error' | 'warning';
  message: string;
}

export function connectionNotice(reachability: HostReachability | null): ConnectionNotice | null {
  switch (reachability) {
    case 'rejected':
      return {
        tone: 'error',
        message:
          'Tript has restarted since this page was opened, and issues a new launch key each time it '
          + 'starts — this page still holds the old one, so it can no longer connect. Open the '
          + 'address Tript printed when it started to carry on.',
      };
    case 'unreachable':
      return {
        tone: 'error',
        message:
          'Tript is not answering. It may have been closed, or restarted with a new launch key — '
          + 'start Tript if it is not running, then open the address it prints.',
      };
    case 'accepted':
      return {
        tone: 'warning',
        message:
          'Tript is running, but its control socket is not answering. Recording and the library stay '
          + 'as they are until it reconnects.',
      };
    default:
      return null;
  }
}
