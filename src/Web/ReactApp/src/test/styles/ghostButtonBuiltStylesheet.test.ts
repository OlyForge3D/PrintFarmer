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
const BUTTON_SHADOW_SELECTOR =
  /:where\(\[data-pf-button\]:not\(\[data-pf-variant=(?:"ghost"|'ghost'|ghost)\],\[data-pf-variant=(?:"link"|'link'|link)\],\[data-pf-variant=(?:"unstyled"|'unstyled'|unstyled)\]\)\)\s*\{([^{}]*)\}/g;
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

function validateButtonShadowCascade(css: string): string[] {
  const violations: string[] = [];
  const layerOrder = findLayerOrder(css);
  const componentsIndex = layerOrder.indexOf('components');
  const utilitiesIndex = layerOrder.indexOf('utilities');
  const componentBlocks = findLayerBlocks(css, 'components');
  const rules = [...css.matchAll(BUTTON_SHADOW_SELECTOR)].map((match) => ({
    index: match.index,
    declarations: parseDeclarations(match[1]),
  }));

  if (componentsIndex < 0 || utilitiesIndex < 0 || componentsIndex >= utilitiesIndex) {
    violations.push(
      `expected components before utilities in built layer order; found ${layerOrder.join(', ')}`,
    );
  }

  if (rules.length !== 1) {
    violations.push(`expected one built shared Button shadow rule; found ${rules.length}`);
  }

  for (const rule of rules) {
    const isInComponents = componentBlocks.some(
      (block) => rule.index > block.openBrace && rule.index < block.closeBrace,
    );
    if (!isInComponents) {
      violations.push(
        `shared Button shadow rule at built stylesheet offset ${rule.index} is outside @layer components`,
      );
    }
    if (!rule.declarations.has('--tw-shadow')) {
      violations.push('shared Button shadow rule must define --tw-shadow');
    }
    if (!rule.declarations.get('box-shadow')?.includes('var(--tw-shadow)')) {
      violations.push('shared Button shadow rule must compose box-shadow with var(--tw-shadow)');
    }
  }

  const exactShadowUtility = /\.shadow-xs(?=\s*\{)/g;
  const shadowXsOffsets = [...css.matchAll(exactShadowUtility)].map((match) => match.index);
  if (shadowXsOffsets.length === 0) {
    violations.push('expected the caller .shadow-xs utility in the built stylesheet');
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

  it('keeps the shared Button shadow default below every caller utility', () => {
    const violations = validateButtonShadowCascade(builtCss);
    expect(violations, violations.join('\n')).toEqual([]);
  });

  it('detects a shared Button shadow rule outside the components layer', () => {
    const leakedRule =
      ":where([data-pf-button]:not([data-pf-variant='ghost'],[data-pf-variant='link']," +
      "[data-pf-variant='unstyled'])){--tw-shadow:0 1px 2px #0000000d;" +
      'box-shadow:var(--tw-shadow)}';
    const mutant = `${builtCss}${leakedRule}`;
    const violations = validateButtonShadowCascade(mutant);

    expect(
      violations.some((violation) => violation.includes('outside @layer components')),
      violations.join('\n'),
    ).toBe(true);
  });
});
