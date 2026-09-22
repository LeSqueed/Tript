// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { ErrorBoundary } from './ErrorBoundary';
import {
  CLIENT_ERROR_PREFIX,
  describeError,
  reportClientError,
  setErrorSinkForTests,
  type ClientErrorReport,
} from './errorReporting';

afterEach(() => {
  cleanup();
  setErrorSinkForTests(null);
  vi.restoreAllMocks();
  delete (window as { external?: unknown }).external;
});

function capture(): ClientErrorReport[] {
  const reports: ClientErrorReport[] = [];
  setErrorSinkForTests((report) => reports.push(report));
  return reports;
}

describe('reportClientError', () => {
  it('sends a report with the error name, message and stack', () => {
    const reports = capture();
    reportClientError('error', new TypeError('bad thing'));

    expect(reports).toHaveLength(1);
    expect(reports[0].kind).toBe('error');
    expect(reports[0].message).toBe('TypeError: bad thing');
    expect(reports[0].stack).toBeTruthy();
  });

  // A failure repeating on every render would otherwise flood the host log.
  it('sends an identical failure only once', () => {
    const reports = capture();
    for (let i = 0; i < 25; i++) reportClientError('render', new Error('loop'));

    expect(reports).toHaveLength(1);
  });

  it('caps a very long message so one error cannot fill the log', () => {
    const { message } = describeError(new Error('x'.repeat(10_000)));

    expect(message.length).toBeLessThan(1100);
  });

  it('never throws, even when the sink does', () => {
    setErrorSinkForTests(() => {
      throw new Error('sink broke');
    });

    expect(() => reportClientError('error', new Error('original'))).not.toThrow();
  });

  it('describes a non-Error rejection reason', () => {
    expect(describeError({ code: 42 }).message).toBe('{"code":42}');
    expect(describeError('plain text').message).toBe('plain text');
  });

  // The bridge still works when the control socket is what broke.
  it('uses the native bridge when the shell provides one', () => {
    setErrorSinkForTests(null);
    const sendMessage = vi.fn();
    (window as { external?: unknown }).external = { sendMessage };

    reportClientError('rejection', new Error('over the bridge'));

    expect(sendMessage).toHaveBeenCalledTimes(1);
    const sent = sendMessage.mock.calls[0][0] as string;
    expect(sent.startsWith(CLIENT_ERROR_PREFIX)).toBe(true);
    expect(JSON.parse(sent.slice(CLIENT_ERROR_PREFIX.length)).message).toBe('Error: over the bridge');
  });
});

function Explodes(): never {
  throw new Error('render failed');
}

describe('ErrorBoundary', () => {
  // Without it a render error leaves a blank window and no trace anywhere.
  it('renders a recovery screen instead of a blank window, and reports the error', () => {
    const reports = capture();
    vi.spyOn(console, 'error').mockImplementation(() => {});

    render(
      <ErrorBoundary>
        <Explodes />
      </ErrorBoundary>,
    );

    expect(screen.getByTestId('ui-error-fallback')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reload window' })).toBeTruthy();
    expect(reports.some((report) => report.kind === 'render' && report.message.includes('render failed'))).toBe(true);
  });

  it('renders its children when nothing fails', () => {
    render(
      <ErrorBoundary>
        <p>all good</p>
      </ErrorBoundary>,
    );

    expect(screen.getByText('all good')).toBeTruthy();
  });
});
