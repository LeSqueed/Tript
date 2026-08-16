// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clip dialog tests. The dialog is a pure renderer over its controller, so the state logic
// (default region, region list updates, combine/separate payload grouping, importProgress surface)
// is exercised through the real `useClipDialog` hook mounted in a probe component, with a `send`
// callback captured to assert the CreateClip payloads.

import { act, cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { ClipDialog } from './clipDialog';
import { useClipDialog, type ClipDialogController } from './useClipDialog';

const session: ContentItem = {
  contentType: 'recording',
  fileName: 'session-1.mp4',
  filePath: 'sessions/2026-08-01/session-1.mp4',
  title: 'Session 1',
  startTime: 0,
  endTime: 100,
};

const clientStub: IpcClient = {
  state: 'disconnected',
  connect: () => {},
  close: () => {},
  send: () => {},
  on: () => () => {},
  onStateChange: () => () => {},
};

interface SentCommand {
  method: string;
  params?: unknown;
}

/**
 * Mount the real hook + dialog. Returns `dialog()` (the live controller, re-read after every
 * state change) and the commands the seam owner would send over the socket.
 */
function probe(send?: (method: string, params?: unknown) => void): {
  dialog: () => ClipDialogController;
  sent: SentCommand[];
} {
  const sent: SentCommand[] = [];
  const ref: { dialog: ClipDialogController | null } = { dialog: null };
  function Probe() {
    const dialog = useClipDialog();
    // Keep the ref fresh across re-renders so the test reads the live controller.
    ref.dialog = dialog;
    return <ClipDialog client={clientStub} dialog={dialog} />;
  }
  render(<Probe />);
  const dialog = () => {
    if (!ref.dialog) {
      throw new Error('probe did not mount');
    }
    return ref.dialog;
  };
  // The seam owner sends `client.send('CreateClip', payload)`. The hook's handler receives the
  // payload directly; the default probe records the same shape the real owner would send.
  const sendFn =
    send ??
    ((method: string, params?: unknown) => {
      sent.push({ method, params });
    });
  act(() =>
    dialog().addImportHandler((content) => {
      sendFn('CreateClip', content);
    }),
  );
  return { dialog, sent };
}

afterEach(() => {
  cleanup();
});

describe('clip dialog — default region', () => {
  it('opening proposes a default region centred on the playbar cursor', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    expect(dialog().regions).toHaveLength(1);
    expect(dialog().regions[0]).toMatchObject({ start: 37, end: 47 });
    const rows = screen.getAllByRole('button', { name: /(Select|Deselect) region 1/ });
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('0:37 – 0:47');
  });

  it('opening selects the proposed region for segment looping', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    expect(dialog().selectedRegionId).toBe(dialog().regions[0].id);
    const row = screen.getByRole('button', { name: /Deselect region 1/ });
    expect(row).toBeTruthy();
  });
});

describe('clip dialog — region list', () => {
  it('creating a region updates the region list', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().addRegion(60, 70));
    expect(dialog().regions).toHaveLength(2);
    expect(dialog().regions[1]).toMatchObject({ start: 60, end: 70 });
  });

  it('extending (resizing) a region updates its start/end in the list', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().addRegion(22, 62, dialog().regions[0].id));
    expect(dialog().regions).toHaveLength(1);
    expect(dialog().regions[0]).toMatchObject({ start: 22, end: 62 });
  });

  it('removing a region drops it from the list and clears the selection', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    const id = dialog().regions[0].id;
    act(() => dialog().removeRegion(id));
    expect(dialog().regions).toHaveLength(0);
    expect(dialog().selectedRegionId).toBeNull();
  });
});

describe('clip dialog — create payloads', () => {
  it('combine sends ONE CreateClip carrying all marked regions as segments', () => {
    const { dialog, sent } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().addRegion(60, 70));
    act(() => dialog().setTitle('My clip'));
    act(() => dialog().create());
    expect(sent).toHaveLength(1);
    const payload = sent[0].params as Record<string, unknown>;
    expect(sent[0].method).toBe('CreateClip');
    expect(payload.outputMode).toBe('combine');
    expect(payload.segments).toEqual([
      { startTime: 37, endTime: 47 },
      { startTime: 60, endTime: 70 },
    ]);
    expect(payload.title).toBe('My clip');
    expect(payload.filePath).toBe('sessions/2026-08-01/session-1.mp4');
  });

  it('separate sends ONE CreateClip per region, each a single segment', () => {
    const { dialog, sent } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().addRegion(60, 70));
    act(() => dialog().setMode('separate'));
    act(() => dialog().create());
    expect(sent).toHaveLength(2);
    const payloads = sent.map((entry) => entry.params as Record<string, unknown>);
    for (const payload of payloads) {
      expect(payload.outputMode).toBe('separate');
      expect(payload.segments as { startTime: number }[]).toHaveLength(1);
    }
    const starts = (payloads.map((p) => (p.segments as { startTime: number }[])[0].startTime)).sort(
      (a, b) => a - b,
    );
    expect(starts).toEqual([37, 60]);
  });
});

describe('clip dialog — importProgress surface', () => {
  it('marks the in-flight clip as importing when the clip is sent', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    const states = Object.values(dialog().progress);
    expect(states).toHaveLength(1);
    expect(states[0]).toMatchObject({ status: 'importing' });
  });

  it('done marks the clip as done', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    act(() => dialog().applyImportProgress({ status: 'done', content: session }));
    const states = Object.values(dialog().progress);
    expect(states[0]).toMatchObject({ status: 'done' });
  });

  it('error marks the clip as error with the message', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    act(() => dialog().applyImportProgress({ status: 'error', error: 'encoder failed' }));
    const states = Object.values(dialog().progress);
    expect(states[0]).toMatchObject({ status: 'error', error: 'encoder failed' });
  });

  it('renders the done/error state in the dialog', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    act(() => dialog().applyImportProgress({ status: 'error', error: 'boom' }));
    expect(screen.getByTestId('clip-progress-error').textContent).toContain('boom');
  });

  it('a done/error with nothing in flight is dropped, not misattributed', () => {
    const { dialog } = probe();
    act(() => dialog().applyImportProgress({ status: 'done', content: session }));
    expect(dialog().progress).toEqual({});
  });
});
