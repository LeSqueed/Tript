// SPDX-License-Identifier: GPL-2.0-or-later

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
