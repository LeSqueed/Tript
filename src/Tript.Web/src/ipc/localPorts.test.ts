// SPDX-License-Identifier: GPL-2.0-or-later

import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import process from 'node:process';
import { describe, expect, it } from 'vitest';
import { CONTENT_SERVER_URL, CONTROL_SOCKET_URL } from './endpoints';

const LOCAL_PORTS_CS = 'src/Tript.App/LocalPorts.cs';
const ENDPOINTS_TS = 'src/Tript.Web/src/ipc/endpoints.ts';
const VITE_CONFIG_TS = 'src/Tript.Web/vitest.config.ts';

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

  it('runs the dev and preview hosts off every port the app binds', () => {
    const declared = [...readFromRepo(VITE_CONFIG_TS).matchAll(/\bport:\s*(\d+)/g)]
      .map((match) => Number(match[1]));

    expect(declared.length).toBe(2);
    expect(declared[0], `${VITE_CONFIG_TS} declares different \`server\` and \`preview\` ports.`)
      .toBe(declared[1]);

    const appPorts = ['Ui', 'Content', 'ControlSocket'].map(backendPort);
    expect(
      appPorts.includes(declared[0]),
      `${VITE_CONFIG_TS} serves on ${declared[0]}, which ${LOCAL_PORTS_CS} already binds. `
        + 'Move the dev server, so it and the app can run at the same time.',
    ).toBe(false);
  });
});
