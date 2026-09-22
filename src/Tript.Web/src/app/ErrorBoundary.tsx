// SPDX-License-Identifier: GPL-2.0-or-later

import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button } from '../components/ui/controls';
import { reportClientError } from './errorReporting';

interface ErrorBoundaryState {
  failed: boolean;
}

// Without a boundary, a render error anywhere unmounts the whole tree and leaves a blank window, with
// nothing recorded on either side of the bridge. Recording keeps running in the host regardless, so
// the fallback says so rather than implying Tript itself has stopped.
export class ErrorBoundary extends Component<{ children: ReactNode }, ErrorBoundaryState> {
  state: ErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    const withComponents = new Error(error.message);
    withComponents.name = error.name;
    withComponents.stack = `${error.stack ?? ''}\n\nComponent stack:${info.componentStack ?? ''}`;
    reportClientError('render', withComponents);
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children;

    return (
      <div className="missing-key" role="alert" data-testid="ui-error-fallback">
        <div className="panel">
          <h2>Tript's window ran into a problem</h2>
          <p className="muted">
            Recording and detection keep running in the background. Reloading the window usually fixes
            this. The details were written to Tript's log.
          </p>
          <Button onClick={() => window.location.reload()}>Reload window</Button>
        </div>
      </div>
    );
  }
}
