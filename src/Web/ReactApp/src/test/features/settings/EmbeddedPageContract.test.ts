import { describe, it, expect } from 'vitest';
import { readFileSync, globSync } from 'node:fs';
import { resolve } from 'node:path';

const SRC = resolve(__dirname, '../../..');
const SHELL = resolve(SRC, 'features/settings/pages/SettingsShell.tsx');
const shellSource = readFileSync(SHELL, 'utf8');

/** Layout primitives the shell wraps content in. They are not pages. */
const LAYOUT = new Set(['SettingsSection', 'Suspense', 'TabLoader']);

/** Index just past the JSX opening tag starting at `from`, respecting nested braces and strings. */
function openingTagEnd(src: string, from: number): number {
  let depth = 0;
  let quote: string | null = null;
  for (let i = from; i < src.length; i += 1) {
    const c = src[i];
    if (quote) {
      if (c === quote) quote = null;
    } else if (c === '"' || c === "'" || c === '`') {
      quote = c;
    } else if (c === '{') {
      depth += 1;
    } else if (c === '}') {
      depth -= 1;
    } else if (c === '>' && depth === 0) {
      return i + 1;
    }
  }
  throw new Error('unterminated JSX opening tag');
}

/** Every opening tag for `<Name` in `src`, with braces balanced. */
function openingTags(src: string, name: string): string[] {
  const tags: string[] = [];
  let cursor = 0;
  for (;;) {
    const at = src.indexOf(`<${name}`, cursor);
    if (at === -1) return tags;
    // Do not match <PageTemplateFoo when looking for <PageTemplate.
    if (/[A-Za-z0-9]/.test(src[at + name.length + 1] ?? '')) {
      cursor = at + 1;
      continue;
    }
    const end = openingTagEnd(src, at + name.length + 1);
    tags.push(src.slice(at, end));
    cursor = end;
  }
}

/** The components the shell mounts as page content, with the props each is given. */
function mountedComponents(): { name: string; props: string }[] {
  const found: { name: string; props: string }[] = [];
  for (const mapName of ['SINGLE_PAGE_CONTENT', 'SUB_PAGE_CONTENT']) {
    const start = shellSource.indexOf(`const ${mapName}: Record<string, ReactNode> = {`);
    expect(start, `${mapName} not found in SettingsShell.tsx`).toBeGreaterThan(-1);
    const map = shellSource.slice(start, shellSource.indexOf('\n};', start));

    for (const name of new Set(map.match(/<([A-Z][A-Za-z0-9]*)/g)?.map((m) => m.slice(1)) ?? [])) {
      if (LAYOUT.has(name)) continue;
      for (const tag of openingTags(map, name)) {
        found.push({ name, props: tag.slice(name.length + 1) });
      }
    }
  }
  return found;
}

/** Source of the component the shell mounts — its own file, or its body if declared in the shell. */
function sourceOf(component: string): string {
  const bare = component.replace(/^Lazy/, '');
  const local = shellSource.match(new RegExp(`^(?:function|const) ${bare}\\b[\\s\\S]*?^\\}`, 'm'));
  if (local) {
    return local[0];
  }
  const matches = globSync(`features/**/${bare}.tsx`, { cwd: SRC });
  expect(matches, `no source file found for ${component}`).not.toHaveLength(0);
  return readFileSync(resolve(SRC, matches[0]), 'utf8');
}

const mounted = mountedComponents();
const names = [...new Set(mounted.map((m) => m.name))];
const withHeader = names.filter((name) => sourceOf(name).includes('<PageTemplate'));
const withoutHeader = names.filter((name) => !withHeader.includes(name));

describe('shell-mounted content honours the embedded contract', () => {
  it('finds the content the shell mounts', () => {
    // A tripwire: if the maps are refactored into another shape, the suite below
    // would silently pass on an empty list.
    expect(mounted.length).toBeGreaterThanOrEqual(20);
    expect(withHeader.length).toBeGreaterThanOrEqual(15);
  });

  it.each(mounted.filter((m) => withHeader.includes(m.name)).map((m) => [m.name, m.props] as const))(
    'the shell mounts %s with embedded',
    (_name, props) => {
      // Every page inside the shell is embedded. No exceptions and no per-page
      // special-casing: an exception is how a second h1 gets back in.
      expect(props).toMatch(/\bembedded\b/);
    },
  );

  it.each(withHeader.map((name) => [name] as const))(
    '%s forwards embedded to every PageTemplate it renders',
    (name) => {
      const tags = openingTags(sourceOf(name), 'PageTemplate');

      expect(tags.length).toBeGreaterThan(0);
      for (const tag of tags) {
        // Loading and error early returns are the ones that get forgotten, and
        // they are exactly the states a shell user sees first.
        expect(tag, `${name} renders a PageTemplate without forwarding embedded`).toMatch(
          /embedded=\{embedded\}/,
        );
      }
    },
  );

  it.each(withHeader.map((name) => [name] as const))(
    '%s leaves the embedded/standalone split to PageTemplate',
    (name) => {
      // A page branching on `embedded` again is how the two paths drift apart:
      // the Create Group button existed twice in PrinterGroupsPage for exactly
      // this reason, once in `actions` and once in an embedded-only block.
      expect(sourceOf(name), `${name} still returns a bare branch on embedded`).not.toMatch(
        /return embedded \?|if \(embedded\)\s*\{?\s*\n?\s*return/,
      );
    },
  );

  it.each(withoutHeader.map((name) => [name] as const))(
    '%s renders no page header, so it needs no embedded prop',
    (name) => {
      // Panels and sections are content, not pages. If one grows a PageTemplate
      // it moves into the group above and must take the prop.
      expect(sourceOf(name)).not.toContain('<PageTemplate');
    },
  );
});
