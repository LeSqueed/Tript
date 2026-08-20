// SPDX-License-Identifier: GPL-2.0-or-later

import type { ReactNode } from 'react';

/**
 * The meta strip above a workspace surface — a range, a count, a retention notice.
 *
 * There is deliberately no heading in here. The shell's topbar already names the route, so a second
 * copy of the same word as an h2 with a tagline under it said nothing twice.
 */
export function WorkspaceMeta({ children }: { children: ReactNode }) {
  return <div className="workspace-meta">{children}</div>;
}

export function StatusDot({ tone = 'success' }: { tone?: 'success' | 'warning' | 'error' | 'neutral' }) {
  return <span className={`status-dot status-dot-${tone}`} aria-hidden="true" />;
}

export function EmptyState({
  title,
  description,
  action,
}: {
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <div className="empty-state">
      <h2>{title}</h2>
      <p>{description}</p>
      {action && <div className="empty-state-action">{action}</div>}
    </div>
  );
}
