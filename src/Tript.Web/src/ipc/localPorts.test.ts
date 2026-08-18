// SPDX-License-Identifier: GPL-2.0-or-later
//
// The three loopback ports exist twice: once in src/Tript.App/LocalPorts.cs and once here in the
// frontend. Nothing can share a constant across that boundary, so this pins the two copies to each
// other by reading the C# file off disk. A port changed on one side only would otherwise show up as
// a UI that reconnects forever, or a player that 404s every video.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import process from 'node:process';
import { describe, expect, it } from 'vitest';
import { CONTENT_SERVER_URL, CONTROL_SOCKET_URL } from './endpoints';

const LOCAL_PORTS_CS = 'src/Tript.App/LocalPorts.cs';
const ENDPOINTS_TS = 'src/Tript.Web/src/ipc/endpoints.ts';
const VITE_CONFIG_TS = 'src/Tript.Web/vitest.config.ts';

// Walks up to the solution file rather than counting '..' segments from this file, so moving the
// test does not silently start reading nothing.
const REPO_ROOT = (() => {
  for (let directory = process.cwd(); ; directory = dirname(directory)) {
    if (existsSync(join(directory, 'Tript.slnx')))
      return directory;
    if (dirname(directory) === directory)
      throw new Error(`No Tript.slnx above ${process.cwd()}; the repository root could not be found.`);
  }
})();

function readFromRepo(repoRelativePath: string): string {
  return readFileSync(join(REPO_ROOT, repoRelativePath), 'utf8');
}

// `internal const int Ui = 2882;` — the declaration, not a mention of the number elsewhere.
function backendPort(name: string): number {
  const match = new RegExp(String.raw`\b${name}\s*=\s*(\d+)\s*;`).exec(readFromRepo(LOCAL_PORTS_CS));
  if (match === null)
    throw new Error(`${LOCAL_PORTS_CS} no longer declares a port named '${name}'.`);
  return Number(match[1]);
}

function mismatch(what: string, name: string, frontendFile: string): string {
  return `${what} disagree. Change whichever side is wrong: the backend's ${name} in `
    + `${LOCAL_PORTS_CS}, or the frontend in ${frontendFile}.`;
}

describe('the loopback ports pinned against the backend', () => {
  it('opens the control socket on the port the backend listens on', () => {
    expect(
      Number(new URL(CONTROL_SOCKET_URL).port),
      mismatch('The control socket ports', 'ControlSocket', ENDPOINTS_TS),
    ).toBe(backendPort('ControlSocket'));
  });

  it('addresses the content server on the port the backend serves from', () => {
    expect(
      Number(new URL(CONTENT_SERVER_URL).port),
      mismatch('The content server ports', 'Content', ENDPOINTS_TS),
    ).toBe(backendPort('Content'));
  });

  // The dev host's port is the one the backend puts on the control socket's Origin allowlist; a
  // `vite dev` on any other port is refused at the WebSocket handshake with a 403.
  it('runs the dev and preview hosts on the port the backend allows as an Origin', () => {
    const declared = [...readFromRepo(VITE_CONFIG_TS).matchAll(/\bport:\s*(\d+)/g)]
      .map((match) => Number(match[1]));

    // Both the `server` and the `preview` block; neither may drift from the other or the backend.
    expect(declared.length).toBe(2);
    expect(declared, mismatch('The UI host ports', 'Ui', VITE_CONFIG_TS))
      .toEqual([backendPort('Ui'), backendPort('Ui')]);
  });
});
