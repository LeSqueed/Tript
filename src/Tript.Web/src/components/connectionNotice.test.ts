// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { connectionNotice } from './connectionNotice';

describe('connectionNotice', () => {
  it('speaks for every answer the probe can give, and only then', () => {
    expect(connectionNotice(null)).toBeNull();
    for (const reachability of ['rejected', 'unreachable', 'accepted'] as const) {
      expect(connectionNotice(reachability), reachability).not.toBeNull();
    }
  });

  it("names the restart, and the new address, when the host refused this page's key", () => {
    const notice = connectionNotice('rejected');
    expect(notice?.tone).toBe('error');
    expect(notice?.message).toMatch(/restarted/i);
    expect(notice?.message).toMatch(/address Tript printed/i);
    expect(notice?.message).not.toMatch(/reload/i);
  });

  it('covers both possibilities when nothing answered at all', () => {
    const notice = connectionNotice('unreachable');
    expect(notice?.tone).toBe('error');
    expect(notice?.message).toMatch(/not answering/i);
    expect(notice?.message).toMatch(/closed/i);
    expect(notice?.message).toMatch(/restarted/i);
  });

  it('blames only the socket when the host still accepts the key', () => {
    const notice = connectionNotice('accepted');
    expect(notice?.tone).toBe('warning');
    expect(notice?.message).toMatch(/control socket/i);
    expect(notice?.message).not.toMatch(/key/i);
  });
});
