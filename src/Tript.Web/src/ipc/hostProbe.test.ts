// SPDX-License-Identifier: GPL-2.0-or-later

import { afterEach, describe, expect, it, vi } from 'vitest';
import { classifyProbeStatus, hostProbeUrl, probeHost } from './hostProbe';
import { captureSessionToken } from './sessionToken';

const ORIGIN = 'http://localhost:8892';

afterEach(() => {
  captureSessionToken('');
});

describe('classifyProbeStatus', () => {
  it('reads 403 as the refusal it is, and every other answer as the host still accepting', () => {
    expect(classifyProbeStatus(403)).toBe('rejected');
    expect(classifyProbeStatus(200)).toBe('accepted');
    expect(classifyProbeStatus(404)).toBe('accepted');
    expect(classifyProbeStatus(500)).toBe('accepted');
  });
});

describe('hostProbeUrl', () => {
  it("carries the page's key, so a 403 is about the key and not about its absence", () => {
    captureSessionToken('?k=deadbeef');
    expect(hostProbeUrl(ORIGIN)).toBe('http://localhost:8892/?k=deadbeef');
  });
});

describe('probeHost', () => {
  it('separates a refused key from a page it still serves', async () => {
    const refuses = vi.fn(async () => new Response('', { status: 403 }));
    await expect(probeHost({ fetchImpl: refuses, origin: ORIGIN })).resolves.toBe('rejected');

    const serves = vi.fn(async () => new Response('<!doctype html>', { status: 200 }));
    await expect(probeHost({ fetchImpl: serves, origin: ORIGIN })).resolves.toBe('accepted');
  });

  it('reports a transport failure as unreachable', async () => {
    const fetchImpl = vi.fn(async () => {
      throw new TypeError('Failed to fetch');
    });
    await expect(probeHost({ fetchImpl, origin: ORIGIN })).resolves.toBe('unreachable');
  });

  it('does not let a cached page answer for a host that is gone', async () => {
    const fetchImpl = vi.fn<typeof globalThis.fetch>(async () => new Response('', { status: 200 }));
    await probeHost({ fetchImpl, origin: ORIGIN });
    expect(fetchImpl.mock.calls[0][1]).toMatchObject({ cache: 'no-store' });
  });
});
