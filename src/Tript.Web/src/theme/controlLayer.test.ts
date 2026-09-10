// SPDX-License-Identifier: GPL-2.0-or-later

import { readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { describe, expect, it } from 'vitest';
import { sourceFiles } from './cssRules';

const SRC_ROOT = join(import.meta.dirname, '..');
const LAYER = join(SRC_ROOT, 'components', 'ui');

const views = () => sourceFiles(SRC_ROOT).filter((file) => !file.startsWith(LAYER));

function withoutComments(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '))
    .replace(/\/\/[^\n]*/g, (m) => ' '.repeat(m.length));
}

function hits(pattern: RegExp): string[] {
  const found: string[] = [];
  for (const file of views()) {
    const source = withoutComments(readFileSync(file, 'utf8'));
    for (const match of source.matchAll(pattern)) {
      const line = source.slice(0, match.index).split('\n').length;
      found.push(`${relative(SRC_ROOT, file)}:${line}`);
    }
  }
  return found.sort();
}

describe('control layer', () => {
  it('renders no control the layer does not style', () => {
    const styled = /className=(?:"[^"]*\b(?:input|select|slider|checkbox|radio)\b|\{[^}]*\b(?:input|select|slider|checkbox|radio)\b)/;
    const bare = [...hits(/<select\b[\s\S]{0,400}?>|<input\b[\s\S]{0,400}?type=["'](?:range|checkbox|radio)["'][\s\S]{0,400}?>/gs)];
    const offenders = bare.filter((location) => {
      const [file, line] = location.split(':');
      const source = withoutComments(readFileSync(join(SRC_ROOT, file), 'utf8')).split('\n');
      const element = source.slice(Number(line) - 1, Number(line) + 10).join('\n');
      return !styled.test(element);
    });
    expect(offenders).toEqual([]);
  });

  it('is not bypassed by reaching for .btn directly', () => {
    expect(hits(/className=["'][^"']*\bbtn\b/g)).toEqual([]);
  });

  it('uses drawn icons rather than unicode glyphs', () => {
    expect(hits(/>[\s]*[◈⌁⚙▶⏸★☆🗑✕][\s]*</gu)).toEqual([]);
  });
});
