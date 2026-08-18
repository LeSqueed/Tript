// SPDX-License-Identifier: GPL-2.0-or-later
//
// The banner for a control socket that is down, in the same bar as the backend's error pushes. Not
// dismissible: nothing in the app works while it is up, and dismissing it would leave a UI that is
// merely empty and silent — which is the failure this exists to stop presenting.

import { connectionNotice } from './connectionNotice';
import type { HostReachability } from '../ipc/hostProbe';

export function ConnectionBanner({ reachability }: { reachability: HostReachability | null }) {
  const notice = connectionNotice(reachability);
  if (notice === null) {
    return null;
  }
  return (
    <div
      className={notice.tone === 'warning' ? 'error-banner warning' : 'error-banner'}
      role="alert"
      data-testid="connection-banner"
    >
      <span className="error-banner-message">{notice.message}</span>
    </div>
  );
}
