// SPDX-License-Identifier: GPL-2.0-or-later
//
// Envelope serialisation — the single place a command becomes a wire frame. The contract is strict
// about what it does NOT send: commands with no arguments carry no `parameters` field at all.

import type { CommandEnvelope, CommandName, CommandParameters } from './protocol';

export type { CommandName, CommandParameters } from './protocol';

/** Build the wire JSON for a command. Omitted parameters ⇒ no `parameters` field on the wire. */
export function serializeCommand(method: CommandName, parameters?: CommandParameters): string {
  const envelope: CommandEnvelope = { method };
  if (parameters !== undefined) {
    envelope.parameters = parameters;
  }
  return JSON.stringify(envelope);
}

/**
 * Parse an incoming backend → frontend frame. Returns null for a malformed frame (no string
 * `method`).
 */
export function parseMessage(raw: string): { method: string; content?: unknown } | null {
  try {
    const value: unknown = JSON.parse(raw);
    if (typeof value !== 'object' || value === null) {
      return null;
    }
    const record = value as Record<string, unknown>;
    if (typeof record.method !== 'string' || record.method.length === 0) {
      return null;
    }
    const content = record.content;
    return {
      method: record.method,
      ...(content !== undefined ? { content } : {}),
    };
  } catch {
    return null;
  }
}
