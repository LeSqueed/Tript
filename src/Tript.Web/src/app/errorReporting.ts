// SPDX-License-Identifier: GPL-2.0-or-later

export const CLIENT_ERROR_PREFIX = 'tript:client-error:';

export type ClientErrorKind = 'render' | 'error' | 'rejection' | 'playback';

export interface ClientErrorReport {
  kind: ClientErrorKind;
  message: string;
  stack?: string;
}

type ErrorSink = (report: ClientErrorReport) => void;

type NativeShellWindow = Window & {
  external?: { sendMessage?: (message: string) => void };
};

const MAX_MESSAGE = 1000;
const MAX_STACK = 4000;

// A failure that repeats on every render or every frame would otherwise flood the host log and push
// out the lines that explain it, so identical reports are only sent once.
const MAX_DISTINCT_REPORTS = 50;
const reported = new Set<string>();

function truncate(value: string, limit: number): string {
  return value.length <= limit ? value : `${value.slice(0, limit)}...`;
}

export function describeError(error: unknown): { message: string; stack?: string } {
  if (error instanceof Error) {
    return {
      message: truncate(`${error.name}: ${error.message}`, MAX_MESSAGE),
      stack: error.stack ? truncate(error.stack, MAX_STACK) : undefined,
    };
  }
  let text: string;
  try {
    text = typeof error === 'string' ? error : JSON.stringify(error);
  } catch {
    text = String(error);
  }
  return { message: truncate(text ?? 'unknown error', MAX_MESSAGE) };
}

// The native bridge is used rather than the control socket because a broken socket is itself one of
// the commonest reasons the UI fails, and because React unmounts the failed tree, closing the socket,
// before an error boundary gets to report. In a plain browser there is no bridge, so the console is
// the only place left.
function defaultSink(report: ClientErrorReport): void {
  const external = (window as NativeShellWindow).external;
  if (external?.sendMessage) {
    external.sendMessage(CLIENT_ERROR_PREFIX + JSON.stringify(report));
    return;
  }
  console.error('Tript UI error', report);
}

let sink: ErrorSink = defaultSink;

export function setErrorSinkForTests(next: ErrorSink | null): void {
  sink = next ?? defaultSink;
  reported.clear();
}

export function reportClientError(kind: ClientErrorKind, error: unknown): void {
  const report: ClientErrorReport = { kind, ...describeError(error) };
  const key = `${report.kind}|${report.message}`;
  if (reported.has(key) || reported.size >= MAX_DISTINCT_REPORTS) return;
  reported.add(key);

  try {
    sink(report);
  } catch {
    // Reporting must never be the thing that breaks the UI.
  }
}

export function installGlobalErrorHandlers(target: Window = window): void {
  target.addEventListener('error', (event) => {
    reportClientError('error', event.error ?? event.message);
  });
  target.addEventListener('unhandledrejection', (event) => {
    reportClientError('rejection', event.reason);
  });
}
