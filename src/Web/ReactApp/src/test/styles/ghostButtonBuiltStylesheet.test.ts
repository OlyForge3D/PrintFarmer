import { execFile } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
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

function paintUtilities(classes: string): string[] {
  return classes
    .split(/\s+/)
    .filter(Boolean)
    .filter((token) => {
      const utility = token.slice(token.lastIndexOf(':') + 1);
      return utility.startsWith('bg-') || utility.startsWith('shadow-');
    });
}

describe('Button variant map — bare variants own no paint (#1102)', () => {
  const entries = readVariantClasses(readFileSync(BUTTON_SOURCE, 'utf8'));

  it('parses every bare variant out of the source', () => {
    for (const variant of BARE_VARIANTS) {
      expect(entries.has(variant), `variant '${variant}' missing from variantClasses`).toBe(true);
    }
  });

  it.each(BARE_VARIANTS)('%s declares no background or shadow utility', (variant) => {
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
 * Scope is deliberately the files whose paint this change unmasked. A repo-wide
 * sweep finds 20 instances; the 16 outside this list live in files this change
 * does not touch and are left to their owners rather than silently rewritten
 * here. Narrow and green is a guard; broad and permanently red is noise.
 */
describe('Button caller contract — unmasked callers gate hover on :enabled (#1102)', () => {
  const OWNED = [
    'common/components/ContextMenu.tsx',
    'features/auth/components/EmailConfirmationBanner.tsx',
    'features/fileBrowser/components/ExplorerView.tsx',
    'features/maintenance/components/ComponentReplacementHistory.tsx',
    'features/models3d/components/3d/GCodeViewer3D.tsx',
    'features/models3d/components/3d/ModelViewer3D.tsx',
    'features/queue/components/ModelFiltersBar.tsx',
    'features/settings/pages/SettingsShell.tsx',
    'features/tasks/components/profile-wizard/PrinterModelSelectionStep.tsx',
  ];

  const SRC_ROOT = resolve(REACT_APP_ROOT, 'src');

  /** Opening `<Button ...>` tags, so we never inspect a button's children. */
  const openingTags = (source: string): { tag: string; line: number }[] => {
    const out: { tag: string; line: number }[] = [];
    const re = /<Button(\s)/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(source))) {
      const end = source.indexOf('>', m.index);
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

  it.each(OWNED)('%s pairs no `disabled` Button with an unguarded hover:', (rel) => {
    const source = readFileSync(join(SRC_ROOT, rel), 'utf8');
    const offenders = openingTags(source)
      .filter(({ tag }) => /\bdisabled[=\s]/.test(tag) && unguardedHover(tag))
      .map(({ line }) => `${rel}:${line}`);

    expect(
      offenders,
      'A plain `hover:` utility also matches :disabled. Now that caller paint is no ' +
        'longer overridden by the variant, these repaint under the pointer on a control ' +
        'the user cannot activate. Prefix the hover with `enabled:`.',
    ).toEqual([]);
  });

  it('detects the defect it is meant to detect', () => {
    const injected = `<Button disabled={busy} className="bg-pf-bg-1 hover:bg-pf-bg-0">x</Button>`;
    const found = openingTags(injected).filter(
      ({ tag }) => /\bdisabled[=\s]/.test(tag) && unguardedHover(tag),
    );
    expect(found).toHaveLength(1);

    const fixed = injected.replace('hover:', 'enabled:hover:');
    expect(
      openingTags(fixed).filter(({ tag }) => /\bdisabled[=\s]/.test(tag) && unguardedHover(tag)),
    ).toHaveLength(0);
  });
});
