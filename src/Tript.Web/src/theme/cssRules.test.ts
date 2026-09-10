// SPDX-License-Identifier: GPL-2.0-or-later

import { describe, expect, it } from 'vitest';
import { normaliseSelector, parseRules, shadowedRules } from './cssRules';

const selectorsOf = (css: string) => parseRules(css).map((rule) => rule.selector);

describe('parseRules', () => {
  it('finds plain top-level rules', () => {
    expect(selectorsOf('.a{color:red}.b{color:blue}')).toEqual(['.a', '.b']);
  });

  it('keeps rules after a block-less at-rule', () => {
    expect(selectorsOf("@import 'x.css';\n.a{color:red}\n.b{color:blue}\n.a{color:green}")).toEqual([
      '.a',
      '.b',
      '.a',
    ]);
  });

  it('keeps rules after @charset', () => {
    expect(selectorsOf("@charset 'utf-8';\n.a{color:red}\n.a{color:blue}")).toEqual(['.a', '.a']);
  });

  it('is not confused by an @ inside a url()', () => {
    expect(selectorsOf('.a{background:url(a@2x.png)}\n.b{color:red}\n.b{color:blue}')).toEqual([
      '.a',
      '.b',
      '.b',
    ]);
  });

  it('is not confused by braces or semicolons inside strings', () => {
    expect(selectorsOf('.a{content:"{;@"}\n.b{color:red}')).toEqual(['.a', '.b']);
  });

  it('records nested rules under their parent', () => {
    const rules = parseRules('.card{color:red;&:hover{color:blue}}');
    expect(rules.map((rule) => rule.selector)).toEqual(['.card', '&:hover']);
    expect(rules[1].context).toEqual(['.card']);
  });

  it('records the at-rule a rule sits inside', () => {
    const rules = parseRules('.a{color:red}@media (max-width:700px){.a{color:blue}}');
    expect(rules[0].context).toEqual([]);
    expect(rules[1].context).toEqual(['@media (max-width:700px)']);
  });

  it('reports the line a selector starts on', () => {
    const rules = parseRules('.a{color:red}\n\n.b{color:blue}');
    expect(rules.map((rule) => rule.line)).toEqual([1, 3]);
  });

  it('counts lines through comments and strings', () => {
    const rules = parseRules('/* one\ntwo */\n.a{content:"three\\nfour"}\n.b{color:red}');
    expect(rules.map((rule) => rule.line)).toEqual([3, 4]);
  });
});

describe('normaliseSelector', () => {
  it('treats the same comma list written two ways as one selector', () => {
    expect(normaliseSelector('.a,.b')).toBe(normaliseSelector('.b, .a'));
  });

  it('collapses internal whitespace', () => {
    expect(normaliseSelector('.a   >   .b')).toBe('.a > .b');
  });
});

describe('shadowedRules', () => {
  it('reports a group rule whose every declaration a later rule overrides', () => {
    const css = `
      .toolbar, .row { background: panel; border-radius: 11px; }
      .toolbar { background: base; border-radius: 6px; }
      .row { background: base; border-radius: 6px; }
    `;
    expect(shadowedRules(css)).toEqual(['.row, .toolbar @ 2 (every declaration overridden later)']);
  });

  it('leaves a shared rule alone when the later rule overrides only some of it', () => {
    const css = `
      .intro h2, .empty h3 { font-family: sans; font-weight: 600; }
      .empty h3 { font-size: 19px; }
    `;
    expect(shadowedRules(css)).toEqual([]);
  });

  it('counts a shorthand as overriding the longhands it resets', () => {
    const css = '.a { border-color: red; }\n.a { border: 1px solid blue; }';
    expect(shadowedRules(css)).toEqual(['.a @ 1 (every declaration overridden later)']);
  });

  it('knows `border` does not reset border-radius', () => {
    const css = '.a { border-radius: 11px; }\n.a { border: 1px solid blue; }';
    expect(shadowedRules(css)).toEqual([]);
  });

  it('does not let a rule in one scope shadow a rule in another', () => {
    const css = '.a { color: red; }\n@media (max-width: 700px) { .a { color: blue; } }';
    expect(shadowedRules(css)).toEqual([]);
  });

  it('ignores a rule that declares nothing', () => {
    expect(shadowedRules('.a { }\n.a { color: red; }')).toEqual([]);
  });
});
