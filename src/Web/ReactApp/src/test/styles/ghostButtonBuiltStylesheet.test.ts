import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import ts from 'typescript';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

const TEST_DIR = dirname(fileURLToPath(import.meta.url));
const REACT_APP_ROOT = resolve(TEST_DIR, '../../..');
const VITE_CLI = resolve(REACT_APP_ROOT, 'node_modules/vite/bin/vite.js');
const INDEX_STYLESHEET = /^assets\/index-[^/]+\.css$/;
const TEXT_INHERIT_CLASS = ['text', 'inherit'].join('-');
const GHOST_SELECTOR =
  /\[data-pf-button\]\[data-pf-variant=(?:"ghost"|'ghost'|ghost)\]\s*\{([^{}]*)\}/g;
const TRANSPARENT_VALUES = new Set([
  'transparent',
  '#0000',
  'rgba(0,0,0,0)',
  'rgb(0 0 0/0)',
]);

interface CssBlock {
  openBrace: number;
  closeBrace: number;
}

interface CssRule {
  index: number;
  declarations: Map<string, string>;
}

function findMatchingBrace(css: string, openBrace: number): number {
  let depth = 0;
  let quote = '';
  let inComment = false;

  for (let index = openBrace; index < css.length; index += 1) {
    const char = css[index];
    const next = css[index + 1];

    if (inComment) {
      if (char === '*' && next === '/') {
        inComment = false;
        index += 1;
      }
      continue;
    }

    if (quote) {
      if (char === '\\') index += 1;
      else if (char === quote) quote = '';
      continue;
    }

    if (char === '/' && next === '*') {
      inComment = true;
      index += 1;
    } else if (char === '"' || char === "'") {
      quote = char;
    } else if (char === '{') {
      depth += 1;
    } else if (char === '}') {
      depth -= 1;
      if (depth === 0) return index;
    }
  }

  throw new Error(`Unclosed CSS block beginning at offset ${openBrace}`);
}

function findLayerBlocks(css: string, layer: string): CssBlock[] {
  const header = new RegExp(`@layer\\s+${layer}\\s*\\{`, 'g');

  return [...css.matchAll(header)].map((match) => {
    const openBrace = match.index + match[0].lastIndexOf('{');
    return {
      openBrace,
      closeBrace: findMatchingBrace(css, openBrace),
    };
  });
}

function findLayerOrder(css: string): string[] {
  const order: string[] = [];

  for (const match of css.matchAll(/@layer\s+([^;{}]+)\s*[;{]/g)) {
    for (const layer of match[1].split(',').map((name) => name.trim())) {
      if (layer && !order.includes(layer)) order.push(layer);
    }
  }

  return order;
}

function parseDeclarations(body: string): Map<string, string> {
  const declarations = new Map<string, string>();

  for (const declaration of body.split(';')) {
    const separator = declaration.indexOf(':');
    if (separator < 0) continue;

    declarations.set(
      declaration.slice(0, separator).trim(),
      declaration.slice(separator + 1).trim(),
    );
  }

  return declarations;
}

function findGhostRules(css: string): CssRule[] {
  return [...css.matchAll(GHOST_SELECTOR)].map((match) => ({
    index: match.index,
    declarations: parseDeclarations(match[1]),
  }));
}

// #1102 moved the paint defaults of these four variants into @layer components for
// the same reason ghost's live there. The declarations differ per variant and are
// not worth pinning, but the property that makes the fix work — the base rule
// exists and sits inside the components layer, below every caller utility — is the
// same one, and is invisible to a className assertion.
const PAINTED_VARIANTS = ['subtle', 'tab', 'toggle', 'link'] as const;

// Matches only the base rule: requiring `]` then `{` excludes the `:hover` and
// `[data-pf-active]` rules, which are layered by the same block anyway.
function variantSelector(variant: string): RegExp {
  return new RegExp(
    `\\[data-pf-button\\]\\[data-pf-variant=(?:"${variant}"|'${variant}'|${variant})\\]\\s*\\{[^{}]*\\}`,
    'g',
  );
}

function validateVariantLayering(css: string): string[] {
  const violations: string[] = [];
  const componentBlocks = findLayerBlocks(css, 'components');

  for (const variant of PAINTED_VARIANTS) {
    const rules = [...css.matchAll(variantSelector(variant))];

    if (rules.length === 0) {
      violations.push(`expected a built ${variant} base rule; found none`);
      continue;
    }

    for (const rule of rules) {
      const isInComponents = componentBlocks.some(
        (block) => rule.index > block.openBrace && rule.index < block.closeBrace,
      );
      if (!isInComponents) {
        violations.push(
          `${variant} base rule at built stylesheet offset ${rule.index} is outside @layer components`,
        );
      }
    }
  }

  return violations;
}

function validateGhostCascade(css: string): string[] {
  const violations: string[] = [];
  const layerOrder = findLayerOrder(css);
  const componentsIndex = layerOrder.indexOf('components');
  const utilitiesIndex = layerOrder.indexOf('utilities');
  const componentBlocks = findLayerBlocks(css, 'components');
  const ghostRules = findGhostRules(css);

  if (componentsIndex < 0 || utilitiesIndex < 0 || componentsIndex >= utilitiesIndex) {
    violations.push(
      `expected components before utilities in built layer order; found ${layerOrder.join(', ')}`,
    );
  }

  if (ghostRules.length !== 1) {
    violations.push(`expected one built ghost base rule; found ${ghostRules.length}`);
  }

  for (const rule of ghostRules) {
    const isInComponents = componentBlocks.some(
      (block) => rule.index > block.openBrace && rule.index < block.closeBrace,
    );
    if (!isInComponents) {
      violations.push(
        `ghost base rule at built stylesheet offset ${rule.index} is outside @layer components`,
      );
    }

    const expected = [
      ['color', 'inherit'],
      ['box-shadow', 'none'],
    ] as const;
    for (const [property, value] of expected) {
      if (rule.declarations.get(property) !== value) {
        violations.push(
          `ghost base rule must declare ${property}:${value}; found ${rule.declarations.get(property) ?? 'nothing'}`,
        );
      }
    }

    for (const property of ['background-color', 'border-color']) {
      const value = rule.declarations.get(property);
      if (!value || !TRANSPARENT_VALUES.has(value)) {
        violations.push(
          `ghost base rule must declare transparent ${property}; found ${value ?? 'nothing'}`,
        );
      }
    }

    if (rule.declarations.has('background')) {
      violations.push('ghost base rule must not use the background shorthand');
    }
  }

  if (css.includes(`.${TEXT_INHERIT_CLASS}{`)) {
    violations.push(
      `built stylesheet unexpectedly emits .${TEXT_INHERIT_CLASS}; Tailwind source detection drifted`,
    );
  }

  return violations;
}

async function buildIndexStylesheet(outDir: string): Promise<string> {
  const execFileAsync = promisify(execFile);
  await execFileAsync(process.execPath, [
    VITE_CLI,
    'build',
    '--outDir',
    outDir,
    '--emptyOutDir',
    '--logLevel',
    'error',
  ], {
    cwd: REACT_APP_ROOT,
    maxBuffer: 20 * 1024 * 1024,
    windowsHide: true,
  });
  const assetsDir = resolve(outDir, 'assets');
  const stylesheets = readdirSync(assetsDir)
    .map((fileName) => `assets/${fileName}`)
    .filter((fileName) => INDEX_STYLESHEET.test(fileName));

  if (stylesheets.length !== 1) {
    throw new Error(
      `Expected one built ${INDEX_STYLESHEET} asset; found ${stylesheets.length}`,
    );
  }

  return readFileSync(resolve(outDir, stylesheets[0]), 'utf8');
}

describe('built stylesheet ghost cascade (#1122)', () => {
  let builtCss = '';
  let outDir = '';

  beforeAll(async () => {
    outDir = mkdtempSync(join(tmpdir(), 'printfarmer-ghost-css-'));
    builtCss = await buildIndexStylesheet(outDir);
  }, 120_000);

  afterAll(() => {
    if (outDir) rmSync(outDir, { recursive: true, force: true });
  });

  it('keeps the ghost paint defaults below caller utilities', () => {
    const violations = validateGhostCascade(builtCss);

    expect(violations, violations.join('\n')).toEqual([]);
  });

  it('detects an unlayered ghost paint rule in the built artifact', () => {
    const leakedRule =
      "[data-pf-button][data-pf-variant='ghost']{" +
      'color:inherit;box-shadow:none;background-color:#0000;border-color:#0000}';
    const mutant = `${builtCss}${leakedRule}`;
    const byteDelta = Buffer.byteLength(mutant) - Buffer.byteLength(builtCss);

    expect(
      byteDelta,
      `falsification injection must change the built artifact; delta was ${byteDelta} bytes`,
    ).toBe(Buffer.byteLength(leakedRule));

    const violations = validateGhostCascade(mutant);
    expect(
      violations.some((violation) => violation.includes('outside @layer components')),
      violations.join('\n'),
    ).toBe(true);
  });

  it('keeps the subtle, tab, toggle and link paint defaults below caller utilities', () => {
    const violations = validateVariantLayering(builtCss);

    expect(violations, violations.join('\n')).toEqual([]);
  });

  it('detects an unlayered subtle paint rule in the built artifact', () => {
    const leakedRule =
      "[data-pf-button][data-pf-variant='subtle']{background-color:#0000;border-color:#0000}";
    const mutant = `${builtCss}${leakedRule}`;
    const byteDelta = Buffer.byteLength(mutant) - Buffer.byteLength(builtCss);

    expect(
      byteDelta,
      `falsification injection must change the built artifact; delta was ${byteDelta} bytes`,
    ).toBe(Buffer.byteLength(leakedRule));

    const violations = validateVariantLayering(mutant);
    expect(
      violations.some(
        (violation) =>
          violation.startsWith('subtle base rule') &&
          violation.includes('outside @layer components'),
      ),
      violations.join('\n'),
    ).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// #1102 source-level guard.
//
// The built-stylesheet assertions above prove the component-layer defaults sit
// where they should. They do NOT prove the variant map stopped declaring paint
// as a utility, because a re-added `bg-transparent` lands in @layer utilities
// and leaves every component-layer rule untouched — the checks above stay green
// while the defect is fully restored. This was verified by injection: adding
// `bg-transparent` back to `subtle` left all four assertions passing even
// though the built artifact then sorted `.bg-transparent` 19,791 bytes after
// `.bg-pf-accent`, i.e. the original bug exactly.
//
// So assert the source contract directly: the bare variants own no background
// or shadow utility in any state.
// ---------------------------------------------------------------------------
const BUTTON_SOURCE = resolve(REACT_APP_ROOT, 'src/common/components/ui/Button.tsx');
const BARE_VARIANTS = ['ghost', 'subtle', 'tab', 'toggle', 'link'] as const;

function readVariantClasses(source: string): Map<string, string> {
  const start = source.indexOf('const variantClasses');
  if (start < 0) throw new Error('variantClasses declaration not found in Button.tsx');
  const end = source.indexOf('\n};', start);
  if (end < 0) throw new Error('variantClasses closing brace not found in Button.tsx');
  const block = source.slice(start, end);
  const entries = new Map<string, string>();
  for (const match of block.matchAll(/^\s*(\w+):\s*'([^']*)'/gm)) {
    entries.set(match[1], match[2]);
  }
  return entries;
}

/**
 * Utilities that paint a property the component-layer default owns.
 *
 * The built base rule declares `color`, `box-shadow`, `background-color` and
 * `border-color`, so a re-add of any of those four reproduces #1102 — not just
 * a background. Matching `bg-`/`shadow-` alone left `text-pf-*`, `border-pf-*`
 * and `ring-pf-*` able to suppress caller paint with the guard still green.
 *
 * Colour-capable prefixes are shared with purely structural utilities
 * (`border-b-2`, `text-left`, `ring-0`), so a bare prefix match would be a
 * false positive. Require evidence the token is actually a colour: a `pf-`
 * design token, an arbitrary value, a CSS-wide colour keyword, or a Tailwind
 * palette shade. The last two matter because `text-white` is already used by
 * the `success` variant, so copying it onto a bare variant is plausible and
 * would otherwise reproduce #1102 with this guard green.
 */
const COLOUR_CAPABLE =
  /^(?:text|border|ring|outline|divide|from|via|to|fill|stroke|accent|caret|decoration)-/;

/** `white`, `black`, `transparent`, `current`, `inherit`. */
const NAMED_COLOUR = /^(?:white|black|transparent|current|inherit)$/;

/**
 * A Tailwind palette shade such as `red-500`. Anchored on a known hue list so
 * that structural tokens sharing the shape — `border-b-2` reduces to `b-2` —
 * cannot match.
 */
const PALETTE_SHADE =
  /^(?:slate|gray|grey|zinc|neutral|stone|red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose)-\d{2,3}$/;

/**
 * Strip Tailwind's important marker. Both spellings are live in this repo —
 * v3's leading `!bg-transparent` (40+ sites) and v4's trailing `justify-start!`
 * (71 sites) — and each defeats a *different* check: a leading `!` breaks
 * `startsWith('bg-')`, a trailing one breaks the `$`-anchored value regexes.
 *
 * An important paint utility is strictly worse than the #1102 defect it would
 * hide: `!important` in @layer utilities beats the @layer components default
 * unconditionally, so it defeats the core fix itself rather than merely winning
 * on source order.
 *
 * `!+` rather than `!` because `text-white!!` is accepted by the compiler.
 */
const stripImportant = (utility: string): string => utility.replace(/^!+/, '').replace(/!+$/, '');

/**
 * Reduce a class token to its bare utility: drop every state variant, then the
 * important marker.
 *
 * The variant separator must be found at bracket depth 0. A naive
 * `lastIndexOf(':')` lands *inside* an arbitrary value — `text-[color:var(--x)]`
 * reduces to `var(--x)]`, which matches nothing and silently reports a paint
 * utility as safe. `Button.tsx` already ships `bg-[var(--pf-button-primary-bg)]`,
 * so that shape is one edit away from being live.
 */
const bareUtility = (token: string): string => {
  let depth = 0;
  let cut = -1;
  for (let i = 0; i < token.length; i += 1) {
    const ch = token[i];
    if (ch === '[') depth += 1;
    else if (ch === ']') depth -= 1;
    else if (ch === ':' && depth === 0) cut = i;
  }
  return stripImportant(token.slice(cut + 1));
};

const classTokens = (classes: string): string[] => classes.split(/\s+/).filter(Boolean);

function paintUtilities(classes: string): string[] {
  return classTokens(classes).filter((token) => {
    const utility = bareUtility(token);
    if (utility === 'shadow') return true;
    if (utility.startsWith('bg-') || utility.startsWith('shadow-')) return true;
    if (!COLOUR_CAPABLE.test(utility)) return false;
    const value = utility.slice(utility.indexOf('-') + 1).split('/')[0];
    return (
      utility.includes('pf-') ||
      utility.includes('[') ||
      NAMED_COLOUR.test(value) ||
      PALETTE_SHADE.test(value)
    );
  });
}

describe('Button variant map — bare variants own no paint (#1102)', () => {
  const entries = readVariantClasses(readFileSync(BUTTON_SOURCE, 'utf8'));

  it('parses every bare variant out of the source', () => {
    for (const variant of BARE_VARIANTS) {
      expect(entries.has(variant), `variant '${variant}' missing from variantClasses`).toBe(true);
    }
  });

  it.each(BARE_VARIANTS)('%s declares no paint utility in any state', (variant) => {
    const offending = paintUtilities(entries.get(variant) ?? '');
    expect(
      offending,
      `variant '${variant}' declares ${offending.join(', ')} as a utility. Utilities land in ` +
        '@layer utilities, the same layer as caller className, so they suppress caller paint ' +
        'on source order. Move the default into @layer components in styles/controls.css, ' +
        "keyed off [data-pf-variant='" + variant + "'].",
    ).toEqual([]);
  });

  it('still allows structural, non-paint utilities', () => {
    expect(paintUtilities(entries.get('tab') ?? '')).toEqual([]);
    expect(entries.get('tab')).toContain('border-b-2');
    expect(entries.get('link')).toContain('px-0');

    // Structural utilities that share a colour-capable prefix must not trip it.
    expect(
      paintUtilities('border border-b-2 ring-0 px-0 text-left text-sm underline rounded-none'),
    ).toEqual([]);
  });

  it('flags every property the component-layer default owns, in any state', () => {
    // background-color, box-shadow, color, border-color — plus prefix forms.
    expect(paintUtilities('bg-pf-accent')).toEqual(['bg-pf-accent']);
    expect(paintUtilities('shadow')).toEqual(['shadow']);
    expect(paintUtilities('shadow-xs')).toEqual(['shadow-xs']);
    expect(paintUtilities('text-pf-accent')).toEqual(['text-pf-accent']);
    expect(paintUtilities('border-pf-accent')).toEqual(['border-pf-accent']);
    expect(paintUtilities('border-l-pf-accent')).toEqual(['border-l-pf-accent']);
    expect(paintUtilities('ring-pf-accent')).toEqual(['ring-pf-accent']);
    expect(paintUtilities('text-[var(--pf-text-inverse)]')).toEqual([
      'text-[var(--pf-text-inverse)]',
    ]);
    expect(paintUtilities('enabled:hover:text-pf-accent')).toEqual(['enabled:hover:text-pf-accent']);
    expect(paintUtilities('dark:focus:border-pf-accent')).toEqual(['dark:focus:border-pf-accent']);
  });

  /**
   * Regression: the `$`-anchored value regexes above (NAMED_COLOUR,
   * PALETTE_SHADE) once missed a trailing `!`, so `text-white!` passed while
   * `text-white` failed. Both important spellings are live in this repo, and an
   * important paint utility is worse than plain #1102 — it beats caller paint
   * unconditionally rather than only on source order.
   */
  it('sees paint through both Tailwind important spellings', () => {
    // Trailing `!` (v4) — the form that slipped past the value regexes.
    expect(paintUtilities('text-white!')).toEqual(['text-white!']);
    expect(paintUtilities('text-black!')).toEqual(['text-black!']);
    expect(paintUtilities('text-red-500!')).toEqual(['text-red-500!']);
    expect(paintUtilities('border-red-500!')).toEqual(['border-red-500!']);
    expect(paintUtilities('ring-white!')).toEqual(['ring-white!']);
    expect(paintUtilities('enabled:hover:text-white!')).toEqual(['enabled:hover:text-white!']);

    // Leading `!` (v3) — breaks the prefix checks instead of the value regexes.
    expect(paintUtilities('!bg-transparent')).toEqual(['!bg-transparent']);
    expect(paintUtilities('!shadow-md')).toEqual(['!shadow-md']);
    expect(paintUtilities('!text-pf-accent')).toEqual(['!text-pf-accent']);
    expect(paintUtilities('dark:!border-pf-accent')).toEqual(['dark:!border-pf-accent']);

    // Structural utilities stay exempt under either spelling.
    expect(paintUtilities('!p-2 !h-auto justify-start! rounded-none!')).toEqual([]);
  });
});

/**
 * #1102 caller contract, second direction: a disabled control must not repaint
 * under the pointer.
 *
 * A plain `hover:` utility matches on `:disabled`. Before the components-layer
 * move, the variant's own `enabled:hover:bg-*` sat later in @layer utilities and
 * overrode the caller, so a caller's unguarded `hover:` never actually painted
 * and the defect was invisible. Freeing caller paint is what lets it through,
 * which makes it this change's fallout rather than a pre-existing caller bug.
 *
 * Scope is every call site this cluster unmasked, found by walking the tree
 * rather than by listing files. An earlier version enumerated nine files by
 * hand; a repo-wide sweep then found ten more offending sites in files that
 * list did not mention, so the list was not a deliberate scope boundary — it
 * was the limit of what had been looked at.
 *
 * `unstyled` is deliberately excluded. It declares no paint and has no
 * components-layer default, so its callers were always in full control and
 * nothing about them changed here. Its unguarded hovers are pre-existing and
 * belong to their owners.
 */
describe('Button caller contract — unmasked callers gate hover on :enabled (#1102)', () => {
  const SRC_ROOT = resolve(REACT_APP_ROOT, 'src');

  /**
   * Variants whose paint this cluster moved into the components layer, and
   * whose callers are therefore newly able to paint. `unstyled` is not one of
   * them — see the note above.
   */
  const UNMASKED_VARIANTS = ['ghost', 'subtle', 'tab', 'toggle', 'link'];

  /**
   * Read the `variant` prop only. Matching bare quoted words anywhere in the
   * tag is wrong: `role="tab"` and `key={tab.name}` both produce a false
   * `tab` hit, which is how a genuinely `unstyled` control got miscounted as
   * fallout during this work. The AST reads the attribute by name, so the
   * confusion is structurally impossible rather than merely guarded against.
   */
  const sourceFiles = (dir: string, acc: string[] = []): string[] => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) sourceFiles(full, acc);
      else if (/\.(tsx|jsx)$/.test(entry)) acc.push(full);
    }
    return acc;
  };

  /**
   * Every `<Button>` element in a file, parsed with the TypeScript compiler.
   *
   * Three hand-rolled parsers preceded this one and each shipped a different
   * tokenisation bug: a tag terminated at the first `>` inside an arrow
   * function; a `className` reader that returned raw template text (so an
   * interpolated `${cond ? 'text-pf-error' : ''}` arrived as `"text-pf-error`
   * with a quote glued on and matched nothing); and a variant reader that could
   * not tell `role="tab"` from `variant="tab"`. The compiler already answers all
   * of these exactly, and `typescript` is already a dependency.
   *
   * Own-versus-nested falls out of the AST for free: `node.attributes` are the
   * Button's own props, so `iconCenter={<Icon className="text-pf-error" />}`
   * belongs to the icon and is never attributed to the Button.
   */
  interface ButtonTag {
    line: number;
    className: string;
    variants: string[];
    disableable: boolean;
    dynamicVariant: boolean;
  }

  /**
   * Every string the expression can contribute, including the literal segments
   * of a template. `class={`a ${cond ? 'b' : 'c'}`}` yields `a`, `b`, `c` — the
   * union of what can render, which is the right conservative reading for a
   * guard.
   */
  const literalsIn = (node: ts.Node): string[] => {
    const out: string[] = [];
    const visit = (n: ts.Node): void => {
      if (ts.isStringLiteral(n) || ts.isNoSubstitutionTemplateLiteral(n)) out.push(n.text);
      else if (ts.isTemplateHead(n) || ts.isTemplateMiddle(n) || ts.isTemplateTail(n)) out.push(n.text);
      // The callback must return void: `ts.forEachChild` treats any truthy
      // return as "stop", which silently drops the rest of the tree.
      ts.forEachChild(n, visit);
    };
    visit(node);
    return out;
  };

  const buttonTags = (source: string, fileName = 'f.tsx'): ButtonTag[] => {
    const sf = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const out: ButtonTag[] = [];

    const read = (node: ts.JsxOpeningElement | ts.JsxSelfClosingElement): void => {
      const tag: ButtonTag = {
        line: sf.getLineAndCharacterOfPosition(node.getStart(sf)).line + 1,
        className: '',
        variants: ['primary'],
        disableable: false,
        dynamicVariant: false,
      };

      for (const attr of node.attributes.properties) {
        if (!ts.isJsxAttribute(attr)) continue;
        const name = attr.name.getText(sf);
        if (name === 'disabled' || name === 'loading') tag.disableable = true;
        else if (name === 'className' && attr.initializer) {
          tag.className = literalsIn(attr.initializer).join(' ');
        } else if (name === 'variant' && attr.initializer) {
          const literals = literalsIn(attr.initializer);
          tag.variants = literals;
          tag.dynamicVariant = literals.length === 0;
        }
      }

      out.push(tag);
    };

    const visit = (node: ts.Node): void => {
      if (
        (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
        node.tagName.getText(sf) === 'Button'
      ) {
        read(node);
      }
      ts.forEachChild(node, visit);
    };

    visit(sf);
    return out;
  };

  /**
   * A variant this cluster unmasked. A `variant={expr}` that resolves to no
   * literal is treated as unmasked rather than skipped: it *may* be one of the
   * five, and a guard that silently ignores what it cannot resolve is the same
   * false-negative shape as the parsers this replaced.
   */
  const isUnmasked = (tag: ButtonTag): boolean =>
    tag.dynamicVariant || tag.variants.some((v) => UNMASKED_VARIANTS.includes(v));

  it('no unmasked-variant Button pairs `disabled` with an unguarded hover:', () => {
    const offenders: string[] = [];

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const tag of buttonTags(readFileSync(file, 'utf8'), file)) {
        if (!tag.disableable || !unguardedHover(tag.className)) continue;
        if (!isUnmasked(tag)) continue;
        offenders.push(`${rel}:${tag.line}`);
      }
    }

    expect(
      offenders,
      'A plain `hover:` utility also matches :disabled. Now that caller paint is no ' +
        'longer overridden by the variant, these repaint under the pointer on a control ' +
        'the user cannot activate. Prefix the hover with `enabled:`.',
    ).toEqual([]);
  });

  /**
   * #1102 caller contract, third direction: the FOREGROUND channel.
   *
   * Every earlier framing of this cluster — the issue, the four merged PRs and
   * the first seven rounds of this branch — described the fallout as "caller
   * backgrounds now paint". The same cascade governs `color`. Pre-fix, `subtle`
   * emitted `text-pf-text-secondary` as a utility that outsorted a caller's
   * `text-pf-error`, so the caller's colour never painted and the site looked
   * fine. Freeing it is what makes the caller's own choice visible, and a fill
   * token is not a text colour: `--pf-error` is tuned to be painted ON, not to
   * be read against a page surface.
   *
   * Measured across all 8 palettes: `text-pf-error` on `bg-pf-panel` lands at
   * 4.41 rest / 4.06 hover in forge, and on `bg-pf-bg-1` at 4.22 — both below
   * AA. Retargeting to `--pf-error-text` lifts the worst palette to 7.52/7.82.
   *
   * `accent` is deliberately absent from the map: no palette defines
   * `--pf-accent-text`, so there is nothing to retarget to. Its remaining call
   * sites are left alone rather than pointed at a token that does not exist.
   * Their worst measured rest contrast is 4.65 — light, on `bg-pf-bg-0`, the
   * alternating-row surface at `SystemLogsContent.tsx:245`. That still passes
   * AA, but the margin is 0.15 rather than the 0.49 an earlier revision of this
   * comment claimed from a surface those sites do not actually sit on.
   *
   * The count is 6 sites / 7 tokens lexically, of which 5 are reachable at
   * runtime: `TagInput.tsx:291` is a ternary whose accent branch is gated on
   * the same condition that selects `variant='primary'`, so its accent spelling
   * never coexists with the bare variant. Two reviewers reported 6 and 5 and
   * both were right about different populations; the difference is recorded
   * rather than resolved because a future edit to that ternary makes 6 correct.
   *
   * Matching is per token via `bareUtility`, not by regex over the raw string:
   * that is what makes it see `!text-pf-error`, `text-pf-error!` and a token
   * arriving from inside a template interpolation.
   */
  it('no unmasked-variant Button uses a fill token as its foreground', () => {
    const RETARGET: Record<string, string> = {
      'text-pf-error': 'text-pf-error-text',
      'text-pf-warning': 'text-pf-warning-text',
      'text-pf-success': 'text-pf-success-text',
    };

    const offenders = new Set<string>();

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const tag of buttonTags(readFileSync(file, 'utf8'), file)) {
        if (!isUnmasked(tag)) continue;
        // Every offending token, not just the first: a site typically carries
        // both a resting and a hover spelling, and reporting one hides the other.
        for (const utility of classTokens(tag.className).map(bareUtility)) {
          if (utility in RETARGET) {
            offenders.add(`${rel}:${tag.line} uses ${utility}, want ${RETARGET[utility]}`);
          }
        }
      }
    }

    expect(
      [...offenders],
      'A fill token used as `color` on a bare variant is no longer suppressed by the ' +
        'variant, so it now paints and is read directly against the page surface. ' +
        'Use the semantic text token instead — it is defined in all 8 palettes.',
    ).toEqual([]);
  });

  // `enabled:hover:` is correct; `group-hover:`/`peer-hover:` key off another
  // element's state and say nothing about this control being disabled.
  const unguardedHover = (className: string) =>
    classTokens(className).some((token) =>
      /(?<!enabled:)(?<!group-)(?<!peer-)hover:/.test(token),
    );

  it('detects the defect it is meant to detect', () => {
    const injected = `<Button disabled={busy} className="bg-pf-bg-1 hover:bg-pf-bg-0">x</Button>`;
    expect(
      buttonTags(injected).filter((t) => t.disableable && unguardedHover(t.className)),
    ).toHaveLength(1);

    const fixed = injected.replace('hover:', 'enabled:hover:');
    expect(
      buttonTags(fixed).filter((t) => t.disableable && unguardedHover(t.className)),
    ).toHaveLength(0);
  });

  /**
   * Regression tests for the parser itself, not for any call site. Each shape
   * below defeated a previous hand-rolled parser and was found by a reviewer
   * injecting a live defect, not by reading the code.
   */
  it('reads past an arrow function in a prop', () => {
    // An earlier parser terminated the tag at the first `>` after `<Button`,
    // which an arrow function supplies — truncating before `disabled` and
    // `className`, so the guard passed on a file that had a defect.
    const withArrow = [
      '<Button',
      '  onClick={() => {',
      '    actionItem.onClick();',
      '  }}',
      '  disabled={actionItem.disabled}',
      '  className="rounded-none hover:bg-pf-bg-1"',
      '>label</Button>',
    ].join('\n');

    const [parsed] = buttonTags(withArrow);
    expect(parsed, 'the opening tag must be found at all').toBeDefined();
    expect(parsed.disableable, 'tag truncated before `disabled` by the `=>`').toBe(true);
    expect(parsed.className, 'tag truncated before `className` by the `=>`').toContain(
      'hover:bg-pf-bg-1',
    );
  });

  it('reads a className out of a template interpolation', () => {
    // A string-scanning reader returned raw template text, so an interpolated
    // token arrived quote-prefixed (`"text-pf-error`) and matched nothing.
    // 18 live call sites lost className tokens this way.
    const interpolated = [
      '<Button variant="ghost" className={`px-2 ${',
      "  active ? 'text-pf-error font-medium' : 'text-pf-text-secondary'",
      '}`}>x</Button>',
    ].join('\n');

    const [parsed] = buttonTags(interpolated);
    expect(parsed.variants).toEqual(['ghost']);
    expect(classTokens(parsed.className).map(bareUtility)).toContain('text-pf-error');
  });

  it('ignores paint on a nested element, and sees the Button own it', () => {
    // A child's own `color` beats inheritance in any layer, so nested icon
    // paint is unchanged by #1102 and must not be attributed to the Button.
    const nested =
      '<Button variant="subtle" className="px-2" iconCenter={<Icon className="text-pf-error" />}>x</Button>';
    expect(classTokens(buttonTags(nested)[0].className).map(bareUtility)).not.toContain(
      'text-pf-error',
    );

    const own =
      '<Button variant="subtle" className="px-2 text-pf-error" iconCenter={<Icon className="w-4" />}>x</Button>';
    expect(classTokens(buttonTags(own)[0].className).map(bareUtility)).toContain('text-pf-error');
  });

  it('treats a trailing bare `disabled` and `loading` as disableable', () => {
    expect(buttonTags('<Button disabled className="hover:bg-pf-bg-1">x</Button>')[0].disableable).toBe(
      true,
    );
    expect(buttonTags('<Button loading className="hover:bg-pf-bg-1">x</Button>')[0].disableable).toBe(
      true,
    );
    // Annotations are not the real prop and disable nothing.
    expect(
      buttonTags('<Button aria-disabled="true" className="hover:bg-pf-bg-1">x</Button>')[0]
        .disableable,
    ).toBe(false);
  });

  it('does not skip a Button whose variant is a non-literal expression', () => {
    // `declaredVariants` used to return [] here, so the tag was silently
    // dropped from both contracts. Unresolvable is treated as unmasked.
    const dynamic = '<Button variant={closeButtonVariant} className="text-pf-error">x</Button>';
    const [parsed] = buttonTags(dynamic);
    expect(parsed.dynamicVariant).toBe(true);
    expect(isUnmasked(parsed)).toBe(true);
  });
});
