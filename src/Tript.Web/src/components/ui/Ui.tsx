// SPDX-License-Identifier: GPL-2.0-or-later

import type { ReactNode } from 'react';

export function WorkspaceIntro({
  title,
  description,
  aside,
}: {
  title: string;
  description?: string;
  aside?: ReactNode;
}) {
  return (
    <div className="workspace-intro">
      <div>
        <h2>{title}</h2>
        {description && <p>{description}</p>}
      </div>
      {aside && <div className="workspace-intro-aside">{aside}</div>}
    </div>
  );
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
      <h3>{title}</h3>
      <p>{description}</p>
      {action && <div className="empty-state-action">{action}</div>}
    </div>
  );
}
