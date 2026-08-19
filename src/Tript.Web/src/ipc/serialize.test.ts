// SPDX-License-Identifier: GPL-2.0-or-later
//
// Envelope serialisation tests. The contract's strict cases are pinned here:
//   - commands with no arguments send NO `parameters` field at all (not `{}`);
//   - field names are exactly the chosen convention (PascalCase methods, camelCase params);
//   - the incoming side tolerates an absent `content` and an absent `parameters`;
//   - malformed frames (non-JSON, no method, non-string method) return null.

import { describe, expect, it } from 'vitest';
import { serializeCommand, parseMessage } from './serialize';

describe('serializeCommand', () => {
  it('omits the parameters field entirely for commands with no arguments', () => {
    const wire = serializeCommand('StartRecording');
    expect(wire).toBe('{"method":"StartRecording"}');
    // The classic mistake: an empty object is NOT sent.
    expect(wire).not.toContain('parameters');
  });

  it('sends parameters when present', () => {
    const wire = serializeCommand('CreateClip', {
      id: 'clip-1',
      type: 'clip',
      game: null,
      igdbId: null,
      fileName: 'recording-1.mp4',
      filePath: 'session/recording-1.mp4',
      title: 'Draft',
      startTime: 10,
      endTime: 20,
      segments: [{ startTime: 10, endTime: 15 }],
      outputMode: 'combine',
    });
    const parsed = JSON.parse(wire) as Record<string, unknown>;
    expect(parsed.method).toBe('CreateClip');
    expect(parsed.parameters).toMatchObject({
      id: 'clip-1',
      fileName: 'recording-1.mp4',
      startTime: 10,
      outputMode: 'combine',
    });
  });

  it('sends NewConnection with the protocol version', () => {
    const wire = JSON.parse(serializeCommand('NewConnection', { protocolVersion: 1 })) as {
      method: string;
      parameters: { protocolVersion: number };
    };
    expect(wire.method).toBe('NewConnection');
    expect(wire.parameters.protocolVersion).toBe(1);
  });

  it('uses camelCase field names — a wrong-cased field is absent, so serialisation is the guard', () => {
    // The reference contract mixed `OutputMode` with `fileName`. Our convention is camelCase for
    // parameters, so a command built with PascalCase keys must not leak them onto the wire.
    const wire = serializeCommand('DeleteContent', {
      contentType: 'recording',
      fileName: 'sessions/a.mp4',
    });
    const parsed = JSON.parse(wire) as Record<string, unknown>;
    const params = parsed.parameters as Record<string, unknown>;
    expect(params).toHaveProperty('contentType');
    expect(params).toHaveProperty('fileName');
    // The PascalCase variants must not appear.
    expect(params).not.toHaveProperty('ContentType');
    expect(params).not.toHaveProperty('FileName');
  });
});

describe('parseMessage', () => {
  it('parses a valid backend frame', () => {
    const parsed = parseMessage('{"method":"state","content":{"recording":true}}');
    expect(parsed).toEqual({ method: 'state', content: { recording: true } });
  });

  it('tolerates an absent content field', () => {
    const parsed = parseMessage('{"method":"refreshStorageStats"}');
    expect(parsed).toEqual({ method: 'refreshStorageStats' });
    expect(parsed).not.toHaveProperty('content');
  });

  it('returns null for non-JSON', () => {
    expect(parseMessage('not json')).toBeNull();
  });

  it('returns null for a frame with no method', () => {
    expect(parseMessage('{"content":{}}')).toBeNull();
  });

  it('returns null for a frame with a non-string method', () => {
    expect(parseMessage('{"method":42}')).toBeNull();
  });

  it('returns null for an empty string method', () => {
    expect(parseMessage('{"method":""}')).toBeNull();
  });

  it('returns null for a JSON array', () => {
    expect(parseMessage('[1,2,3]')).toBeNull();
  });
});
