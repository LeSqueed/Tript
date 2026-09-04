// SPDX-License-Identifier: GPL-2.0-or-later
//
// The clip dialog tests. The dialog is a pure renderer over its controller, so the state logic
// (default region, region list updates, combine/separate payload grouping, importProgress surface)
// is exercised through the real `useClipDialog` hook mounted in a probe component, with a `send`
// callback captured to assert the CreateClip payloads.

import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import type { ContentItem } from '../../ipc/protocol';
import type { IpcClient } from '../../ipc/websocketClient';
import { ClipDialog } from './clipDialog';
import { MIN_REGION_SECONDS } from './clipModel';
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
 * The measured media length these tests run against, seconds. It matches `session.endTime` on
 * purpose — the tests below check region/payload logic, not bounds provenance — but it is passed in
 * as the *measured* length, because that is the only kind the controller accepts.
 */
const MEASURED_SECONDS = 100;

/**
 * Mount the real hook + dialog. Returns `dialog()` (the live controller, re-read after every
 * state change) and the commands the seam owner would send over the socket.
 */
function probe(
  send?: (method: string, params?: unknown) => void,
  currentTime = 0,
): {
  dialog: () => ClipDialogController;
  sent: SentCommand[];
} {
  const sent: SentCommand[] = [];
  const ref: { dialog: ClipDialogController | null } = { dialog: null };
  function Probe() {
    const dialog = useClipDialog(MEASURED_SECONDS);
    // Keep the ref fresh across re-renders so the test reads the live controller.
    ref.dialog = dialog;
    return <ClipDialog client={clientStub} dialog={dialog} currentTime={currentTime} />;
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
    const rows = screen.getAllByRole('button', { name: /(Select|Deselect) clip 1/ });
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('0:37 – 0:47');
  });

  it('opening selects the proposed region for segment looping', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    expect(dialog().selectedRegionId).toBe(dialog().regions[0].id);
    const row = screen.getByRole('button', { name: /Deselect clip 1/ });
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

describe('clip dialog — marking segments (the in/out path from the player)', () => {
  it('keeps segments marked before the dialog was ever opened, instead of reseeding a proposal', () => {
    const { dialog } = probe();
    // The player attaches the session as it plays, then the user marks with I/O.
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(10, 20));
    act(() => dialog().openDialog(session, 42));
    expect(dialog().regions).toHaveLength(1);
    expect(dialog().regions[0]).toMatchObject({ start: 10, end: 20 });
  });

  it('the first mark replaces the untouched default proposal and becomes the loop target', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    expect(dialog().regions[0]).toMatchObject({ start: 37, end: 47 });
    act(() => dialog().markRegion(60, 70));
    expect(dialog().regions).toHaveLength(1);
    expect(dialog().regions[0]).toMatchObject({ start: 60, end: 70 });
    expect(dialog().selectedRegionId).toBe(dialog().regions[0].id);
  });

  it('further marks are added next to the existing ones', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().markRegion(60, 70));
    act(() => dialog().markRegion(10, 20));
    expect(dialog().regions.map((region) => [region.start, region.end])).toEqual([
      [60, 70],
      [10, 20],
    ]);
  });

  it('an adjusted proposal is the user\'s own region: a later mark is added, not swapped in', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    const proposalId = dialog().regions[0].id;
    act(() => dialog().updateRegion(proposalId, 30, 40));
    act(() => dialog().markRegion(60, 70));
    expect(dialog().regions).toHaveLength(2);
    expect(dialog().regions[0]).toMatchObject({ id: proposalId, start: 30, end: 40 });
  });

  it('marking orders and clamps the points, and refuses a span with no length', () => {
    const { dialog } = probe();
    act(() => dialog().attachSession(session));
    // Out point before the in point, and past the session end.
    act(() => dialog().markRegion(70, 60));
    act(() => dialog().markRegion(95, 500));
    expect(dialog().regions.map((region) => [region.start, region.end])).toEqual([
      [60, 70],
      [95, 100],
    ]);
    // Both points on the same frame — nothing to clip.
    act(() => dialog().markRegion(30, 30));
    expect(dialog().regions).toHaveLength(2);
  });

  it('closing the dialog keeps the marked segments; clearing them is explicit', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().markRegion(60, 70));
    act(() => dialog().closeDialog());
    expect(dialog().regions).toHaveLength(1);
    act(() => dialog().openDialog(session, 10));
    expect(dialog().regions).toHaveLength(1);
    fireEvent.click(screen.getByRole('button', { name: 'Clear all clips' }));
    expect(dialog().regions).toHaveLength(0);
    expect(dialog().selectedRegionId).toBeNull();
  });

  it('attaching a different session drops the marks made on the previous one', () => {
    const { dialog } = probe();
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(60, 70));
    act(() => dialog().attachSession({ ...session, filePath: 'sessions/other.mp4' }));
    expect(dialog().regions).toHaveLength(0);
  });

  it('the empty state says how to mark a segment', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().removeRegion(dialog().regions[0].id));
    const empty = screen.getByText(/No clips yet/).textContent ?? '';
    expect(empty).toMatch(/press I/);
    expect(empty).toMatch(/then O/);
    expect(empty).toMatch(/M for a 10s clip/);
  });
});

describe('clip dialog — adjusting a region', () => {
  it('typed bounds change the region, including the seeded default', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    fireEvent.change(screen.getByLabelText('Clip 1 start, seconds'), { target: { value: '30' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 start, seconds'));
    fireEvent.change(screen.getByLabelText('Clip 1 end, seconds'), { target: { value: '52.5' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 end, seconds'));
    expect(dialog().regions[0]).toMatchObject({ start: 30, end: 52.5 });
    expect(screen.getByRole('button', { name: /Deselect clip 1/ }).textContent).toContain('0:30 – 0:52');
  });

  it('an end typed before the start parks against it instead of inverting the region', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    fireEvent.change(screen.getByLabelText('Clip 1 end, seconds'), { target: { value: '10' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 end, seconds'));
    expect(dialog().regions[0]).toMatchObject({ start: 37, end: 37 + MIN_REGION_SECONDS });
  });

  it('bounds beyond the session are clamped to it', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    fireEvent.change(screen.getByLabelText('Clip 1 end, seconds'), { target: { value: '500' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 end, seconds'));
    fireEvent.change(screen.getByLabelText('Clip 1 start, seconds'), { target: { value: '-20' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 start, seconds'));
    expect(dialog().regions[0]).toMatchObject({ start: 0, end: 100 });
  });

  it('a blank or unparsable field leaves the region alone', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    fireEvent.change(screen.getByLabelText('Clip 1 start, seconds'), { target: { value: '' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 start, seconds'));
    expect(dialog().regions[0]).toMatchObject({ start: 37, end: 47 });
  });

  it('snapping the start to the playhead moves only the start', () => {
    const { dialog } = probe(undefined, 40);
    act(() => dialog().openDialog(session, 42));
    fireEvent.click(screen.getByRole('button', { name: 'Start clip 1 where you are' }));
    expect(dialog().regions[0]).toMatchObject({ start: 40, end: 47 });
  });

  it('snapping the end to the playhead moves only the end', () => {
    const { dialog } = probe(undefined, 40);
    act(() => dialog().openDialog(session, 42));
    fireEvent.click(screen.getByRole('button', { name: 'End clip 1 where you are' }));
    expect(dialog().regions[0]).toMatchObject({ start: 37, end: 40 });
  });

  it('an adjusted region reaches CreateClip with the corrected bounds', () => {
    const { dialog, sent } = probe();
    act(() => dialog().openDialog(session, 42));
    fireEvent.change(screen.getByLabelText('Clip 1 start, seconds'), { target: { value: '20' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 start, seconds'));
    fireEvent.change(screen.getByLabelText('Clip 1 end, seconds'), { target: { value: '30' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 end, seconds'));
    act(() => dialog().create());
    const payload = sent[0].params as Record<string, unknown>;
    expect(payload.segments).toEqual([{ startTime: 20, endTime: 30 }]);
    expect(payload.startTime).toBe(20);
    expect(payload.endTime).toBe(30);
  });

  it('adjusting one region never disturbs the others', () => {
    const { dialog } = probe();
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(10, 20));
    act(() => dialog().markRegion(60, 70));
    const first = dialog().regions[0].id;
    // Dragged/typed right across the second region: an edit is not a mark, so it merges nothing away.
    act(() => dialog().updateRegion(first, 55, 80));
    expect(dialog().regions).toHaveLength(2);
    expect(dialog().regions[0]).toMatchObject({ id: first, start: 55, end: 80 });
    expect(dialog().regions[1]).toMatchObject({ start: 60, end: 70 });
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
    expect(payloads.map((payload) => payload.title)).toEqual(['Session 1 - 01', 'Session 1 - 02']);
    const starts = (payloads.map((p) => (p.segments as { startTime: number }[])[0].startTime)).sort(
      (a, b) => a - b,
    );
    expect(starts).toEqual([37, 60]);
  });

  it('two marked segments become two segments of one clip in combine mode', () => {
    const { dialog, sent } = probe();
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(60, 70));
    act(() => dialog().markRegion(10, 20));
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    expect(sent).toHaveLength(1);
    const payload = sent[0].params as Record<string, unknown>;
    expect(payload.outputMode).toBe('combine');
    expect(payload.segments).toEqual([
      { startTime: 60, endTime: 70 },
      { startTime: 10, endTime: 20 },
    ]);
  });

  it('two marked segments become two CreateClip calls in separate mode', () => {
    const { dialog, sent } = probe();
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(60, 70));
    act(() => dialog().markRegion(10, 20));
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().setMode('separate'));
    act(() => dialog().create());
    expect(sent).toHaveLength(2);
    const starts = sent
      .map((entry) => (entry.params as Record<string, unknown>).startTime as number)
      .sort((a, b) => a - b);
    expect(starts).toEqual([10, 60]);
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
    const id = Object.keys(dialog().progress)[0];
    act(() => dialog().applyImportProgress({ id, status: 'done', content: session }));
    const states = Object.values(dialog().progress);
    expect(states[0]).toMatchObject({ status: 'done' });
  });

  it('error marks the clip as error with the message', () => {
    const { dialog } = probe();
    act(() => dialog().openDialog(session, 42));
    act(() => dialog().create());
    const id = Object.keys(dialog().progress)[0];
    act(() => dialog().applyImportProgress({ id, status: 'error', error: 'encoder failed' }));
    const states = Object.values(dialog().progress);
    expect(states[0]).toMatchObject({ status: 'error', error: 'encoder failed' });
  });

  it('a done/error with nothing in flight is dropped, not misattributed', () => {
    const { dialog } = probe();
    act(() => dialog().applyImportProgress({ id: 'missing', status: 'done', content: session }));
    expect(dialog().progress).toEqual({});
  });
});

// ---------------------------------------------------------------------------------------------
// The clippable duration: the bound the controller holds its regions inside.
//
// The player resolves it from the media itself and passes it in. Until it does, the length in force
// is provisional — the session's declared `endTime`, or nothing at all — so the controller has to do
// two things when the real one arrives: stop allowing edits beyond it, and correct the regions that
// were marked before it was known.

/** A probe whose clippable duration can change under the controller, as the media's metadata does. */
function boundedProbe(initialDuration: number): {
  dialog: () => ClipDialogController;
  setDuration(seconds: number): void;
  sent: SentCommand[];
} {
  const sent: SentCommand[] = [];
  const ref: {
    dialog: ClipDialogController | null;
    setDuration: ((seconds: number) => void) | null;
  } = { dialog: null, setDuration: null };
  function Probe() {
    const [duration, setDuration] = useState(initialDuration);
    const dialog = useClipDialog(duration);
    ref.dialog = dialog;
    ref.setDuration = setDuration;
    return <ClipDialog client={clientStub} dialog={dialog} currentTime={0} />;
  }
  render(<Probe />);
  const dialog = () => {
    if (!ref.dialog) {
      throw new Error('probe did not mount');
    }
    return ref.dialog;
  };
  act(() =>
    dialog().addImportHandler((content) => {
      sent.push({ method: 'CreateClip', params: content });
    }),
  );
  return {
    dialog,
    setDuration: (seconds: number) => {
      act(() => ref.setDuration?.(seconds));
    },
    sent,
  };
}

describe('clip dialog — the clippable duration', () => {
  it('clamps marks to the media length even when the session claims to be longer', () => {
    // The record says 100s (see `session`); the media reported 8s, and the media is what gets cut.
    const { dialog } = boundedProbe(8);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(2, 50));
    expect(dialog().regions.map((region) => [region.start, region.end])).toEqual([[2, 8]]);
    expect(dialog().duration).toBe(8);
  });

  it('reconciles regions marked against a longer duration when the real one arrives', () => {
    const { dialog, setDuration } = boundedProbe(100);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(1, 3));
    act(() => dialog().markRegion(5, 15));
    act(() => dialog().markRegion(37, 47));
    expect(dialog().regions).toHaveLength(3);

    setDuration(8);
    // Straddling the real end → truncated; entirely beyond it → dropped, not squashed to a sliver.
    expect(dialog().regions.map((region) => [region.start, region.end])).toEqual([
      [1, 3],
      [5, 8],
    ]);
  });

  it('drops the loop selection with the region it was pointing at', () => {
    const { dialog, setDuration } = boundedProbe(100);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(37, 47));
    expect(dialog().selectedRegionId).toBe(dialog().regions[0].id);
    setDuration(8);
    expect(dialog().regions).toHaveLength(0);
    expect(dialog().selectedRegionId).toBeNull();
  });

  it('sends nothing when no marked segment survives the real duration', () => {
    const { dialog, sent, setDuration } = boundedProbe(100);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(37, 47));
    act(() => dialog().openDialog(session, 42));
    setDuration(8);
    act(() => dialog().create());
    expect(sent).toHaveLength(0);
  });

  it('refuses every edit while no length is known, and proposes nothing', () => {
    const { dialog, sent } = boundedProbe(0);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(10, 20));
    act(() => dialog().addRegion(10, 20));
    expect(dialog().regions).toHaveLength(0);
    // Opening the dialog seeds a default proposal only when it can be placed inside the media.
    act(() => dialog().openDialog(session, 42));
    expect(dialog().regions).toHaveLength(0);
    act(() => dialog().create());
    expect(sent).toHaveLength(0);
    expect(screen.getByText(/length unknown/)).toBeTruthy();
  });

  it('proposes a default region inside a media shorter than the default length', () => {
    const { dialog } = boundedProbe(3);
    act(() => dialog().openDialog(session, 42));
    expect(dialog().regions.map((region) => [region.start, region.end])).toEqual([[0, 3]]);
  });

  it('clamps a typed bound to the media length, not the declared one', () => {
    const { dialog } = boundedProbe(8);
    act(() => dialog().openDialog(session, 4));
    fireEvent.change(screen.getByLabelText('Clip 1 end, seconds'), { target: { value: '95' } });
    fireEvent.blur(screen.getByLabelText('Clip 1 end, seconds'));
    expect(dialog().regions[0]).toMatchObject({ end: 8 });
    expect(dialog().regions[0].start).toBeGreaterThanOrEqual(0);
  });

  it('sends only the part of a segment that exists', () => {
    const { dialog, sent, setDuration } = boundedProbe(100);
    act(() => dialog().attachSession(session));
    act(() => dialog().markRegion(5, 60));
    act(() => dialog().openDialog(session, 10));
    setDuration(8);
    act(() => dialog().create());
    expect(sent).toHaveLength(1);
    const payload = sent[0].params as Record<string, unknown>;
    expect(payload.segments).toEqual([{ startTime: 5, endTime: 8 }]);
    expect(payload.startTime).toBe(5);
    expect(payload.endTime).toBe(8);
  });
});
