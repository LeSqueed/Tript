// SPDX-License-Identifier: GPL-2.0-or-later

import type { ReactNode } from 'react';
import { Button } from './controls';

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

export function ActionBar({
  leading,
  trailing,
  children,
  'data-testid': testId,
}: {
  leading: ReactNode;
  children?: ReactNode;
  trailing?: ReactNode;
  'data-testid'?: string;
}) {
  return (
    <div className="action-bar" data-testid={testId}>
      {leading}
      {children}
      {trailing ? <div className="action-bar-trailing">{trailing}</div> : null}
    </div>
  );
}

export function FilterMismatchEmptyState({
  total,
  noun = 'item',
  onClearFilters,
}: {
  total: number;
  noun?: string;
  onClearFilters?: () => void;
}) {
  return (
    <EmptyState
      title="Nothing fits this view"
      description={`None of your ${total} ${noun}${total === 1 ? '' : 's'} matches these filters.`}
      action={onClearFilters ? <Button variant="primary" onClick={onClearFilters}>Clear filters</Button> : undefined}
    />
  );
}
