// SPDX-License-Identifier: GPL-2.0-or-later
//
// A small CSS rule scanner, used by the theme guard tests.
//
// It exists because the guards' first implementation matched brace pairs with a regex and treated
// every `@` as opening a block. That failed *open*: a block-less at-rule (`@import 'x.css';`) made
// it swallow the next real rule, and an `@` inside a url() turned a declaration into a bogus
// selector — either way duplicate selectors went unreported and the guard passed. A guard that goes
// quiet is worse than no guard, so this is a real (small) tokenizer, and it has its own tests.

import { readdirSync } from 'node:fs';
import { join } from 'node:path';

export interface CssRule {
  /** Selector text, normalised: comma parts trimmed and sorted, so `.a,.b` and `.b, .a` are one key. */
  selector: string;
  /** Enclosing at-rule / parent-rule preludes, outermost first. Empty at the top level. */
  context: string[];
  /** 1-based line the selector starts on, so a failure can point somewhere. */
  line: number;
}

export function normaliseSelector(raw: string): string {
  return raw
    .split(',')
    .map((part) => part.trim().replace(/\s+/g, ' '))
    .filter(Boolean)
    .sort()
    .join(', ');
}

export function parseRules(css: string): CssRule[] {
  const rules: CssRule[] = [];
  let index = 0;
  let line = 1;

  function scanBlock(context: string[]): void {
    let prelude = '';
    let preludeLine = line;
    let started = false;

    const resetPrelude = () => {
      prelude = '';
      started = false;
      preludeLine = line;
    };

    while (index < css.length) {
      const char = css[index];

      // Comments carry no rules but do carry newlines.
      if (char === '/' && css[index + 1] === '*') {
        const close = css.indexOf('*/', index + 2);
        const stop = close === -1 ? css.length : close + 2;
        for (let scan = index; scan < stop; scan += 1) {
          if (css[scan] === '\n') line += 1;
        }
        index = stop;
        continue;
      }

      // Strings are opaque: braces, semicolons and @ inside them mean nothing.
      if (char === '"' || char === "'") {
        const quote = char;
        prelude += char;
        index += 1;
        while (index < css.length && css[index] !== quote) {
          if (css[index] === '\\' && index + 1 < css.length) {
            prelude += css[index];
            index += 1;
          }
          if (css[index] === '\n') line += 1;
          prelude += css[index];
          index += 1;
        }
        prelude += css[index] ?? '';
        index += 1;
        continue;
      }

      if (char === '\n') {
        line += 1;
        prelude += char;
        index += 1;
        continue;
      }

      // A statement: a declaration, or a block-less at-rule such as @import / @charset. Neither is
      // a rule, and crucially neither consumes what follows.
      if (char === ';') {
        index += 1;
        resetPrelude();
        continue;
      }

      if (char === '}') {
        index += 1;
        return;
      }

      if (char === '{') {
        index += 1;
        const text = prelude.trim().replace(/\s+/g, ' ');
        if (text.startsWith('@')) {
          scanBlock([...context, text]);
        } else if (text.length > 0) {
          const selector = normaliseSelector(text);
          rules.push({ selector, context, line: preludeLine });
          // Nested rules belong to this one, so a repeat inside it is still a repeat.
          scanBlock([...context, selector]);
        } else {
          scanBlock(context);
        }
        resetPrelude();
        continue;
      }

      if (!started && !/\s/.test(char)) {
        started = true;
        preludeLine = line;
      }
      prelude += char;
      index += 1;
    }
  }

  scanBlock([]);
  return rules;
}

/** Every stylesheet the app ships, as absolute paths. */
export function stylesheetPaths(root: string = join(import.meta.dirname, '..')): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const full = join(root, entry.name);
    if (entry.isDirectory()) {
      found.push(...stylesheetPaths(full));
    } else if (entry.name.endsWith('.css')) {
      found.push(full);
    }
  }
  return found;
}
