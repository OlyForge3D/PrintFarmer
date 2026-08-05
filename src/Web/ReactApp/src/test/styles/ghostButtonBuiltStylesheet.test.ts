import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
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

function paintUtilities(classes: string): string[] {
  return classes
    .split(/\s+/)
    .filter(Boolean)
    .filter((token) => {
      const utility = token.slice(token.lastIndexOf(':') + 1);
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
   * fallout during this work.
   */
  const declaredVariants = (tag: string): string[] => {
    const prop = tag.match(/\svariant=(?:"([^"]*)"|'([^']*)'|\{([^}]*)\})/);
    if (!prop) return ['primary'];
    const literal = prop[1] ?? prop[2];
    if (literal !== undefined) return [literal];
    return [...(prop[3] ?? '').matchAll(/'([a-z]+)'|"([a-z]+)"/g)].map((m) => m[1] ?? m[2]);
  };

  const sourceFiles = (dir: string, acc: string[] = []): string[] => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) sourceFiles(full, acc);
      else if (/\.(tsx|jsx)$/.test(entry)) acc.push(full);
    }
    return acc;
  };

  it('no unmasked-variant Button pairs `disabled` with an unguarded hover:', () => {
    const offenders: string[] = [];

    for (const file of sourceFiles(SRC_ROOT)) {
      const rel = relative(SRC_ROOT, file).replace(/\\/g, '/');
      for (const { tag, line } of openingTags(readFileSync(file, 'utf8'))) {
        if (!isDisableable(tag) || !unguardedHover(tag)) continue;
        if (!declaredVariants(tag).some((v) => UNMASKED_VARIANTS.includes(v))) continue;
        offenders.push(`${rel}:${line}`);
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
   * Opening `<Button ...>` tags, so we never inspect a button's children.
   *
   * The tag terminator cannot be the first `>` after `<Button`: an arrow
   * function in any prop (`onClick={() => …}`) contains one, which truncates the
   * tag before `disabled` and before `className` and makes this guard silently
   * pass on a live defect. Scan instead for the `>` at brace depth zero and
   * outside string literals.
   */
  const openingTags = (source: string): { tag: string; line: number }[] => {
    const out: { tag: string; line: number }[] = [];
    const re = /<Button(?=[\s/>])/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(source))) {
      let depth = 0;
      let quote: string | null = null;
      let end = -1;

      for (let i = m.index + '<Button'.length; i < source.length; i += 1) {
        const ch = source[i];

        if (quote) {
          if (ch === quote && source[i - 1] !== '\\') quote = null;
          continue;
        }
        if (ch === '"' || ch === "'" || ch === '`') {
          quote = ch;
          continue;
        }
        if (ch === '{') depth += 1;
        else if (ch === '}') depth -= 1;
        else if (ch === '>' && depth === 0) {
          end = i;
          break;
        }
      }

      if (end === -1) continue;
      out.push({
        tag: source.slice(m.index, end),
        line: source.slice(0, m.index).split('\n').length,
      });
    }
    return out;
  };

  // `enabled:hover:` is correct; `group-hover:`/`peer-hover:` key off another
  // element's state and say nothing about this control being disabled.
  const unguardedHover = (tag: string) =>
    /(?<!enabled:)(?<!group-)(?<!peer-)hover:/.test(tag);

  /**
   * Whether the control can reach the disabled state.
   *
   * `Button.tsx` renders `disabled={disabled || loading}`, so `loading`
   * disables just as surely as `disabled` does and must be treated the same.
   * The value is optional: `<Button disabled>` is a valid trailing attribute,
   * so the name may be followed by `=`, whitespace, `/`, `>` or end of tag.
   * The lookbehind keeps `aria-disabled` and `data-loading` out — those are
   * annotations, not the real prop, and do not disable anything.
   */
  const isDisableable = (tag: string) =>
    /(?<![-\w])(?:disabled|loading)(?:[=\s/>]|$)/.test(tag);

  it('detects the defect it is meant to detect', () => {
    const injected = `<Button disabled={busy} className="bg-pf-bg-1 hover:bg-pf-bg-0">x</Button>`;
    const found = openingTags(injected).filter(
      ({ tag }) => isDisableable(tag) && unguardedHover(tag),
    );
    expect(found).toHaveLength(1);

    const fixed = injected.replace('hover:', 'enabled:hover:');
    expect(
      openingTags(fixed).filter(({ tag }) => isDisableable(tag) && unguardedHover(tag)),
    ).toHaveLength(0);
  });

  /**
   * Regression test for the parser itself, not for any call site.
   *
   * An earlier version terminated the tag at the first `>` after `<Button`. Any
   * arrow function in a prop supplies one, so the extracted tag stopped at
   * `<Button onClick={() =` — before `disabled` and before `className`. The
   * guard then reported zero offenders on a file that had one, which is
   * indistinguishable from a clean file. The shape below is the live
   * ContextMenu.tsx one that slipped through.
   */
  it('reads past an arrow function in a prop', () => {
    const withArrow = [
      '<Button',
      '  onClick={() => {',
      '    actionItem.onClick();',
      '  }}',
      '  disabled={actionItem.disabled}',
      '  className="rounded-none hover:bg-pf-bg-1"',
      '>label</Button>',
    ].join('\n');

    const [parsed] = openingTags(withArrow);
    expect(parsed, 'the opening tag must be found at all').toBeDefined();
    expect(parsed.tag, 'tag truncated before `disabled` by the `=>`').toContain('disabled=');
    expect(parsed.tag, 'tag truncated before `className` by the `=>`').toContain('hover:bg-pf-bg-1');

    expect(
      openingTags(withArrow).filter(
        ({ tag }) => isDisableable(tag) && unguardedHover(tag),
      ),
      'an unguarded hover behind an arrow-function prop must still be reported',
    ).toHaveLength(1);
  });

  it('treats a trailing bare `disabled` and `loading` as disableable', () => {
    const offends = (tag: string) =>
      openingTags(tag).filter(
        ({ tag: t }) => isDisableable(t) && unguardedHover(t),
      ).length;

    // `disabled` last, with no value: the earlier `disabled[=\s]` predicate
    // required a following `=` or space and so could not see this.
    expect(
      offends('<Button className="hover:bg-pf-bg-0" disabled>x</Button>'),
      'a trailing valueless `disabled` still disables the control',
    ).toBe(1);

    // Button renders disabled={disabled || loading}, so loading disables too.
    expect(
      offends('<Button loading={busy} className="hover:bg-pf-bg-0">x</Button>'),
      '`loading` disables the control just as `disabled` does',
    ).toBe(1);

    // ...but annotations are not the prop and disable nothing.
    expect(
      offends('<Button aria-disabled="true" className="hover:bg-pf-bg-0">x</Button>'),
      '`aria-disabled` is an annotation, not the disabling prop',
    ).toBe(0);
    expect(
      offends('<Button data-loading="1" className="hover:bg-pf-bg-0">x</Button>'),
      '`data-loading` is an annotation, not the disabling prop',
    ).toBe(0);
  });
});
