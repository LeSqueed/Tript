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
  /** Declarations this rule makes, in source order. */
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
        noteDeclaration(prelude);
        resetPrelude();
        continue;
      }

      if (char === '}') {
        index += 1;
        // A final declaration needs no trailing semicolon.
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
          // Nested rules belong to this one, so a repeat inside it is still a repeat.
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

/**
 * Selectors declared more than once in the same scope, as `selector (Nx) @ line,line`.
 *
 * The key is the whole selector text, so `.a, .b {}` and a later `.b {}` are different keys and are
 * not reported. That is deliberate: a shared rule plus a specific override is ordinary CSS, and
 * flagging it would bury the appended-replacement-block smell this exists to catch.
 */
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

/**
 * Properties declared twice within a single rule. The first is always dead. `.settings-tab` carried
 * `border: none` immediately followed by `border: 1px solid transparent`, which neither duplicatesIn
 * (one rule, one selector) nor shadowedRules (one rule, nothing later) could see.
 */
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

/**
 * Which longhand properties a shorthand resets. Deliberately conservative: an unlisted shorthand
 * simply covers nothing, so the shadow analysis below under-reports rather than accusing a live
 * rule of being dead. `border` is the one that needs care — it does NOT reset border-radius.
 */
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

/**
 * Rules every one of whose declarations is overridden by a later rule at the same specificity — so
 * the rule renders nothing and is pure dead weight.
 *
 * This is the case duplicatesIn cannot see: `.a, .b { background: x }` followed by `.a { background:
 * y }` and `.b { background: z }` uses three different selector keys, yet the group is inert. That
 * exact shape appeared in TrashView.css, where an appended "replacement" block sat ABOVE the rules
 * it meant to replace and therefore never rendered at all.
 *
 * Specificity is compared by identical selector-part text, so no specificity maths is needed: the
 * same part written the same way always has the same specificity.
 */
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
