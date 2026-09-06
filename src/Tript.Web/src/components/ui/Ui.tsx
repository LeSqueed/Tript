// SPDX-License-Identifier: GPL-2.0-or-later

import type { ReactNode } from 'react';
import { Button } from './controls';

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

/**
 * The bar that sits above a list when a selection is live: a "N selected" count, the actions that
 * apply to the selection, and one action pushed to the far edge. The library's select mode and the
 * trash's toolbar are the same chrome, so they share this surface rather than restyling it twice.
 */
export function ActionBar({
  leading,
  trailing,
  children,
  'data-testid': testId,
}: {
  /** The "N selected" count, or (in a mode-less variant) the range/count line. */
  leading: ReactNode;
  /** The actions that apply to the current selection. */
  children?: ReactNode;
  /** One action set apart from the rest, pushed to the trailing edge. */
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

/**
 * The empty state shown when content exists but the current filters hide all of it. Distinct from a
 * truly empty surface: without it a too-narrow filter is indistinguishable from a broken backend, and
 * the one useful instruction (clear the filters) would be invisible. The library, the sessions page
 * and the trash all show it, differing only in the noun for what is being counted.
 */
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
