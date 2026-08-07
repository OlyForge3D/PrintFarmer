import { readFileSync, readdirSync } from 'node:fs';
import { extname, join, relative } from 'node:path';
import ts from 'typescript';

export type ThemeReferenceKind = 'utility' | 'arbitrary-value' | 'custom-property';

export interface ThemeReference {
  file: string;
  line: number;
  column: number;
  token: string;
  source: string;
  kind: ThemeReferenceKind;
  utilityPrefix?: string;
}

export interface ThemeFinding extends ThemeReference {
  reason: string;
}

export interface CssFacts {
  declarations: Map<string, string>;
  mappings: Map<string, string>;
}

export interface RawCustomPropertyAllowance {
  file: string;
  token: string;
  reason: string;
}

export interface SourceText {
  file: string;
  text: string;
}

/**
 * These properties are assigned by React at runtime rather than declared in a
 * stylesheet. Keep this list site-specific: a global token-only exemption would
 * hide the same misspelling everywhere else in the tree.
 */
export const RAW_CUSTOM_PROPERTY_ALLOWLIST: readonly RawCustomPropertyAllowance[] = [
  {
    file: 'common/components/PageTemplate.tsx',
    token: '--pf-floating-bar-inset',
    reason: 'optional runtime layout inset with a local 0px fallback',
  },
  {
    file: 'features/printers/utils/homeButtonStyle.ts',
    token: '--pf-home-bg',
    reason: 'runtime bridge property assigned in the returned React style object',
  },
  {
    file: 'features/printers/utils/homeButtonStyle.ts',
    token: '--pf-home-bg-hover',
    reason: 'runtime bridge property assigned in the returned React style object',
  },
  {
    file: 'features/printers/utils/homeButtonStyle.ts',
    token: '--pf-home-bg-active',
    reason: 'runtime bridge property assigned in the returned React style object',
  },
];

/**
 * The ordinary-utility matcher is intentionally inverted: any class-shaped
 * prefix followed by `-pf-*` is checked unless the prefix is explicitly known
 * to address a non-colour namespace.
 *
 * `data-pf-*` strings are component marker attributes, not utilities.
 * `font-pf-*` utilities resolve through Tailwind's `--font-*` namespace and are
 * covered by the typography contract rather than this colour ratchet.
 */
const NON_COLOUR_PREFIX_ALLOWLIST = new Set(['data', 'font']);

const SOURCE_EXTENSIONS = new Set(['.ts', '.tsx', '.css']);
const EXCLUDED_DIRECTORIES = new Set(['__tests__', 'dist', 'node_modules', 'test']);

const stripComments = (source: string): string =>
  source
    .replace(/\/\*[\s\S]*?\*\//g, (comment) => comment.replace(/[^\n]/g, ' '))
    .replace(/\/\/[^\n]*/g, (comment) => ' '.repeat(comment.length));

const lineAndColumn = (source: string, offset: number): Pick<ThemeReference, 'line' | 'column'> => {
  const before = source.slice(0, offset);
  const lastLineBreak = before.lastIndexOf('\n');
  return {
    line: before.split('\n').length,
    column: offset - lastLineBreak,
  };
};

const stripImportant = (utility: string): string =>
  utility.replace(/^!+/, '').replace(/!+$/, '');

const stripVariants = (token: string): string => {
  let depth = 0;
  let cut = -1;
  for (let index = 0; index < token.length; index += 1) {
    const character = token[index];
    if (character === '[') depth += 1;
    else if (character === ']') depth -= 1;
    else if (character === ':' && depth === 0) cut = index;
  }
  return token.slice(cut + 1);
};

const stripModifier = (utility: string): string => {
  let depth = 0;
  for (let index = 0; index < utility.length; index += 1) {
    const character = utility[index];
    if (character === '[') depth += 1;
    else if (character === ']') depth -= 1;
    else if (character === '/' && depth === 0) return utility.slice(0, index);
  }
  return utility;
};

const normalizedUtility = (token: string): string =>
  stripModifier(stripImportant(stripVariants(token)).replace(/^-/, ''));

const customPropertyReferences = (text: string): RegExpStringIterator<RegExpExecArray> =>
  text.matchAll(/var\(\s*(--(?:color-)?pf-[a-z0-9-]+)(?=\s*[,)])/g);

function scanFragment(
  fragment: string,
  file: string,
  source: string,
  baseOffset: number,
  scanUtilities = true,
): ThemeReference[] {
  const references: ThemeReference[] = [];
  const arbitraryRanges: Array<{ start: number; end: number; source: string }> = [];

  for (const match of fragment.matchAll(/[^\s"'`]+/g)) {
    const rawToken = match[0].replace(/^[({,]+/, '').replace(/[)},;]+$/, '');
    const tokenOffset = baseOffset + (match.index ?? 0);
    const utility = normalizedUtility(rawToken);

    if (utility.includes('[var(')) {
      arbitraryRanges.push({
        start: match.index ?? 0,
        end: (match.index ?? 0) + match[0].length,
        source: rawToken,
      });
      continue;
    }

    if (!scanUtilities || utility.includes('var(')) continue;
    if (utility.startsWith('--')) continue;
    const marker = utility.indexOf('-pf-');
    if (marker <= 0) continue;

    const utilityPrefix = utility.slice(0, marker);
    if (NON_COLOUR_PREFIX_ALLOWLIST.has(utilityPrefix)) continue;

    const tokenName = utility.slice(marker + 1);
    if (!/^pf-[a-z0-9-]+$/.test(tokenName)) continue;
    const location = lineAndColumn(source, tokenOffset);
    references.push({
      file,
      ...location,
      token: tokenName,
      source: rawToken,
      kind: 'utility',
      utilityPrefix,
    });
  }

  for (const match of customPropertyReferences(fragment)) {
    const matchOffset = match.index ?? 0;
    const arbitrary = arbitraryRanges.find(
      (range) => matchOffset >= range.start && matchOffset < range.end,
    );
    const absoluteOffset = baseOffset + matchOffset;
    references.push({
      file,
      ...lineAndColumn(source, absoluteOffset),
      token: match[1],
      source: arbitrary?.source ?? match[0],
      kind: arbitrary ? 'arbitrary-value' : 'custom-property',
    });
  }

  return references;
}

function scanTypeScript(source: string, file: string): ThemeReference[] {
  const scriptKind = file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  const sourceFile = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, scriptKind);
  const references: ThemeReference[] = [];

  const visit = (node: ts.Node): void => {
    if (
      ts.isStringLiteral(node) ||
      ts.isNoSubstitutionTemplateLiteral(node) ||
      ts.isTemplateHead(node) ||
      ts.isTemplateMiddle(node) ||
      ts.isTemplateTail(node)
    ) {
      const nodeStart = node.getStart(sourceFile);
      const textStart = source.indexOf(node.text, nodeStart);
      references.push(
        ...scanFragment(node.text, file, source, textStart >= 0 ? textStart : nodeStart),
      );
    }
    ts.forEachChild(node, visit);
  };

  visit(sourceFile);
  return references;
}

export function scanThemeText(text: string, file: string): ThemeReference[] {
  if (file.endsWith('.css')) {
    const withoutComments = stripComments(text);
    const references = scanFragment(withoutComments, file, withoutComments, 0, false);
    for (const match of withoutComments.matchAll(/@apply\s+([^;]+);/g)) {
      const utilities = match[1];
      const offset = (match.index ?? 0) + match[0].indexOf(utilities);
      references.push(...scanFragment(utilities, file, withoutComments, offset));
    }
    return references;
  }
  return scanTypeScript(text, file);
}

export function collectThemeSources(root: string): SourceText[] {
  const sources: SourceText[] = [];
  const walk = (directory: string): void => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      if (entry.isDirectory() && EXCLUDED_DIRECTORIES.has(entry.name)) continue;
      const fullPath = join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
        continue;
      }
      if (!SOURCE_EXTENSIONS.has(extname(entry.name))) continue;
      if (/\.(?:test|spec)\.[^.]+$/.test(entry.name)) continue;
      sources.push({
        file: relative(root, fullPath).replace(/\\/g, '/'),
        text: readFileSync(fullPath, 'utf8'),
      });
    }
  };
  walk(root);
  return sources;
}

export function cssFacts(sources: readonly SourceText[]): CssFacts {
  const declarations = new Map<string, string>();
  const mappings = new Map<string, string>();

  for (const { file, text } of sources) {
    if (!file.endsWith('.css')) continue;
    const css = stripComments(text);
    for (const match of css.matchAll(/(--pf-[a-z0-9-]+)\s*:([^;]*);/g)) {
      declarations.set(match[1], match[2].trim());
    }
    for (const match of css.matchAll(/(--color-pf-[a-z0-9-]+)\s*:([^;]*);/g)) {
      mappings.set(match[1], match[2].trim());
    }
  }

  return { declarations, mappings };
}

const THEME_SOURCE = /^design-system\/themes\/([^/]+\.css)$/;

/**
 * Builds one resolution graph per selectable theme. Shared CSS (including
 * base.css and the @theme mappings in index.css) is merged into every graph;
 * theme declarations remain isolated so one theme cannot mask another's
 * missing token or alias cycle.
 */
export function cssFactsByTheme(sources: readonly SourceText[]): Map<string, CssFacts> {
  const themedSources = sources.filter(
    (source) => THEME_SOURCE.test(source.file) && !source.file.endsWith('/base.css'),
  );
  const themedFiles = new Set(themedSources.map((source) => source.file));
  const shared = cssFacts(sources.filter((source) => !themedFiles.has(source.file)));

  return new Map(
    themedSources.map((source) => {
      const themed = cssFacts([source]);
      return [
        source.file.replace(/^design-system\/themes\//, ''),
        {
          declarations: new Map([...shared.declarations, ...themed.declarations]),
          mappings: new Map([...shared.mappings, ...themed.mappings]),
        },
      ];
    }),
  );
}

const allowanceFor = (
  reference: ThemeReference,
  allowlist: readonly RawCustomPropertyAllowance[],
): RawCustomPropertyAllowance | undefined =>
  allowlist.find(
    (allowance) => allowance.file === reference.file && allowance.token === reference.token,
  );

const aliasTargets = (value: string): string[] =>
  [...customPropertyReferences(value)].map((match) => match[1]);

function resolvesCustomProperty(token: string, facts: CssFacts, seen = new Set<string>()): boolean {
  if (seen.has(token)) return false;
  seen.add(token);

  const value = token.startsWith('--color-')
    ? facts.mappings.get(token)
    : facts.declarations.get(token);
  if (value === undefined) return false;

  const aliases = aliasTargets(value);
  return (
    aliases.length === 0 ||
    aliases.every((alias) => resolvesCustomProperty(alias, facts, new Set(seen)))
  );
}

function mappingTokens(reference: ThemeReference): string[] {
  if (reference.kind !== 'utility') return [reference.token];
  const colourMapping = `--color-${reference.token}`;
  if (reference.utilityPrefix === 'ring') {
    return [colourMapping, `--ring-color-${reference.token}`];
  }
  if (reference.utilityPrefix === 'ring-offset') {
    return [colourMapping, `--ring-offset-color-${reference.token}`];
  }
  return [colourMapping];
}

export function deadThemeReferences(
  references: readonly ThemeReference[],
  facts: CssFacts,
  allowlist: readonly RawCustomPropertyAllowance[] = RAW_CUSTOM_PROPERTY_ALLOWLIST,
): ThemeFinding[] {
  const findings = new Map<string, ThemeFinding>();

  for (const reference of references) {
    if (allowanceFor(reference, allowlist)) continue;
    const candidates = mappingTokens(reference);
    if (candidates.some((candidate) => resolvesCustomProperty(candidate, facts))) continue;

    const key = [
      reference.file,
      reference.line,
      reference.column,
      reference.token,
      reference.kind,
    ].join(':');
    findings.set(key, {
      ...reference,
      reason:
        reference.kind === 'utility'
          ? `no resolvable Tailwind mapping for ${reference.source}`
          : `custom property ${reference.token} does not resolve to a declared value`,
    });
  }

  return [...findings.values()].sort(
    (left, right) =>
      left.file.localeCompare(right.file) ||
      left.line - right.line ||
      left.column - right.column ||
      left.token.localeCompare(right.token),
  );
}

export function deadThemeReferencesByTheme(
  references: readonly ThemeReference[],
  factsByTheme: ReadonlyMap<string, CssFacts>,
  allowlist: readonly RawCustomPropertyAllowance[] = RAW_CUSTOM_PROPERTY_ALLOWLIST,
): ThemeFinding[] {
  const findings = new Map<string, ThemeFinding>();

  for (const reference of references) {
    if (allowanceFor(reference, allowlist)) continue;
    const candidates = mappingTokens(reference);
    const unresolvedThemes = [...factsByTheme]
      .filter(([, facts]) => !candidates.some((candidate) => resolvesCustomProperty(candidate, facts)))
      .map(([theme]) => theme)
      .sort();
    if (unresolvedThemes.length === 0) continue;

    const key = [
      reference.file,
      reference.line,
      reference.column,
      reference.token,
      reference.kind,
    ].join(':');
    const baseReason =
      reference.kind === 'utility'
        ? `no resolvable Tailwind mapping for ${reference.source}`
        : `custom property ${reference.token} does not resolve to a declared value`;
    findings.set(key, {
      ...reference,
      reason: `${baseReason}; unresolved in ${unresolvedThemes.join(', ')}`,
    });
  }

  return [...findings.values()].sort(
    (left, right) =>
      left.file.localeCompare(right.file) ||
      left.line - right.line ||
      left.column - right.column ||
      left.token.localeCompare(right.token),
  );
}

export function formatThemeFinding(finding: ThemeFinding): string {
  return `${finding.file}:${finding.line}:${finding.column} ${finding.token} (${finding.kind}) - ${finding.reason}`;
}
