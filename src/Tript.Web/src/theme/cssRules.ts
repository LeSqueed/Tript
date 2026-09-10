// SPDX-License-Identifier: GPL-2.0-or-later

import { readdirSync } from 'node:fs';
import { join } from 'node:path';

export interface CssRule {
  selector: string;
  context: string[];
  line: number;
  declarations: { property: string; value: string }[];
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

  function scanBlock(context: string[], owner?: CssRule): void {
    const noteDeclaration = (text: string) => {
      const colon = text.indexOf(':');
      if (owner && colon > 0) {
        owner.declarations.push({
          property: text.slice(0, colon).trim().toLowerCase(),
          value: text.slice(colon + 1).trim().replace(/\s+/g, ' '),
        });
      }
    };
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

      if (char === '/' && css[index + 1] === '*') {
        const close = css.indexOf('*/', index + 2);
        const stop = close === -1 ? css.length : close + 2;
        for (let scan = index; scan < stop; scan += 1) {
          if (css[scan] === '\n') line += 1;
        }
        index = stop;
        continue;
      }

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

      if (char === ';') {
        index += 1;
        noteDeclaration(prelude);
        resetPrelude();
        continue;
      }

      if (char === '}') {
        index += 1;
        noteDeclaration(prelude);
        return;
      }

      if (char === '{') {
        index += 1;
        const text = prelude.trim().replace(/\s+/g, ' ');
        if (text.startsWith('@')) {
          scanBlock([...context, text]);
        } else if (text.length > 0) {
          const selector = normaliseSelector(text);
          const rule: CssRule = { selector, context, line: preludeLine, declarations: [] };
          rules.push(rule);
          scanBlock([...context, selector], rule);
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

export function duplicatesIn(css: string): string[] {
  const scopes = new Map<string, number[]>();
  for (const rule of parseRules(css)) {
    const key = `${rule.context.join(' > ')}||${rule.selector}`;
    const lines = scopes.get(key) ?? [];
    lines.push(rule.line);
    scopes.set(key, lines);
  }
  return [...scopes.entries()]
    .filter(([, lines]) => lines.length > 1)
    .map(([key, lines]) => `${key.split('||')[1]} (${lines.length}x) @ ${lines.join(',')}`)
    .sort();
}

export function repeatedPropertiesIn(css: string): string[] {
  const found: string[] = [];
  for (const rule of parseRules(css)) {
    const seen = new Set<string>();
    const repeated = new Set<string>();
    for (const { property } of rule.declarations) {
      if (seen.has(property)) {
        repeated.add(property);
      }
      seen.add(property);
    }
    for (const property of repeated) {
      found.push(`${rule.selector} @ ${rule.line} declares ${property} twice`);
    }
  }
  return found.sort();
}

export function styledClassesIn(css: string): Set<string> {
  const classes = new Set<string>();
  for (const rule of parseRules(css)) {
    for (const part of rule.selector.split(',')) {
      const leading = part.trim().match(/^\.[a-z0-9-]+/i);
      if (leading) {
        classes.add(leading[0]);
      }
    }
  }
  return classes;
}

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

export function sourceFiles(root: string = join(import.meta.dirname, '..')): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const full = join(root, entry.name);
    if (entry.isDirectory()) {
      found.push(...sourceFiles(full));
    } else if (entry.name.endsWith('.tsx') && !entry.name.endsWith('.test.tsx')) {
      found.push(full);
    }
  }
  return found;
}

export function moduleFiles(root: string = join(import.meta.dirname, '..')): string[] {
  const found: string[] = [];
  for (const entry of readdirSync(root, { withFileTypes: true })) {
    const full = join(root, entry.name);
    if (entry.isDirectory()) {
      found.push(...moduleFiles(full));
    } else if (entry.name.endsWith('.ts') && !entry.name.endsWith('.test.ts') && !entry.name.endsWith('.d.ts')) {
      found.push(full);
    }
  }
  return found;
}

const COVERS: Record<string, (property: string) => boolean> = {
  border: (p) => /^border(-(top|right|bottom|left))?(-(color|width|style))?$/.test(p),
  background: (p) => p.startsWith('background'),
  margin: (p) => p.startsWith('margin'),
  padding: (p) => p.startsWith('padding'),
  font: (p) => p.startsWith('font') || p === 'line-height',
  transition: (p) => p.startsWith('transition'),
  overflow: (p) => p.startsWith('overflow'),
  flex: (p) => p.startsWith('flex'),
  gap: (p) => p === 'gap' || p === 'row-gap' || p === 'column-gap',
  inset: (p) => ['inset', 'top', 'right', 'bottom', 'left'].includes(p),
  'border-radius': (p) => p.endsWith('radius'),
};

const isCovered = (property: string, by: string[]): boolean =>
  by.includes(property) || by.some((candidate) => COVERS[candidate]?.(property) ?? false);

const partsOf = (selector: string): string[] => selector.split(',').map((part) => part.trim());

export function shadowedRules(css: string): string[] {
  const rules = parseRules(css);
  const dead: string[] = [];

  for (const [position, rule] of rules.entries()) {
    if (rule.declarations.length === 0) {
      continue;
    }
    const scope = rule.context.join(' > ');
    const everyPartShadowed = partsOf(rule.selector).every((part) => {
      const laterProperties = rules
        .slice(position + 1)
        .filter((other) => other.context.join(' > ') === scope && partsOf(other.selector).includes(part))
        .flatMap((other) => other.declarations.map((declaration) => declaration.property));
      return rule.declarations.every(({ property }) => isCovered(property, laterProperties));
    });
    if (everyPartShadowed) {
      dead.push(`${rule.selector} @ ${rule.line} (every declaration overridden later)`);
    }
  }
  return dead;
}
