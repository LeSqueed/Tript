// SPDX-License-Identifier: GPL-2.0-or-later
//
// The copy is the deliverable here, so it is pinned: a rejected key must not be answered with
// "reload", because the stale key is in this page's own URL and a reload presents it again — the
// host answers the plain-text refusal and the app is gone entirely.

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
    // Honest about the two it cannot tell apart from silence alone.
    expect(notice?.message).toMatch(/closed/i);
    expect(notice?.message).toMatch(/restarted/i);
  });

  // The key is fine and the host is up — blaming the key here would be the confident wrong message.
  it('blames only the socket when the host still accepts the key', () => {
    const notice = connectionNotice('accepted');
    expect(notice?.tone).toBe('warning');
    expect(notice?.message).toMatch(/control socket/i);
    expect(notice?.message).not.toMatch(/key/i);
  });
});
