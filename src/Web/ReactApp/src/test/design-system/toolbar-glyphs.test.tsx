import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

const sourceRoot = path.resolve(__dirname, '../..');
const controlGlyph = /[\u2190-\u2193\u2039\u203a\u25b6\u25bc\u25b2\u25a6\u2630\u21bb]/u;

function findControlGlyphs(source: string): string[] {
  const file = ts.createSourceFile('component.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const found: string[] = [];
  function visit(node: ts.Node, inControl: boolean) {
    let control = inControl;
    if (ts.isJsxElement(node)) {
      const name = node.openingElement.tagName.getText(file);
      control ||= /^(Button|button|FilterChip|option)$/.test(name);
    }
    if (control && (ts.isJsxText(node) || ts.isStringLiteral(node))) {
      const text = ts.isJsxText(node) ? node.text.trim() : node.text;
      const descriptiveTitle = ts.isJsxAttribute(node.parent) && node.parent.name.getText(file) === 'title'
        && /\S\s+\u2192\s+\S/u.test(text);
      if (controlGlyph.test(text) && !descriptiveTitle) {
        const { line } = file.getLineAndCharacterOfPosition(node.getStart(file));
        found.push(`${line + 1}: ${text}`);
      }
    }
    ts.forEachChild(node, child => visit(child, control));
  }
  visit(file, false);
  return found;
}

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (entry.name === '__tests__') return [];
    const file = path.join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(file) : file.endsWith('.tsx') ? [file] : [];
  });
}

describe('Toolbar glyph vocabulary', () => {
  it('rejects a representative control-glyph regression while preserving prose and jog exceptions', () => {
    expect(findControlGlyphs('<Button>\u2191</Button>')).toEqual(['1: \u2191']);
    expect(findControlGlyphs('<Button>{ascending ? "\\u2191" : "\\u2193"}</Button>')).toHaveLength(2);
    expect(findControlGlyphs('<Button>Refresh \u21bb</Button>')).toHaveLength(1);
    expect(findControlGlyphs('<p>Version A \u2192 B</p><ControlPadButton>\u2191</ControlPadButton>')).toEqual([]);
    expect(findControlGlyphs('<Button title="Version A \u2192 B">Details</Button>')).toEqual([]);
  });

  it('contains no Unicode control glyphs on migrated toolbar surfaces', () => {
    const directories = [
      'features/queue', 'features/printers', 'features/cameras', 'features/auto-dispatch',
      'features/fileBrowser', 'features/files', 'features/filamentManagement', 'features/catalog',
      'features/projects', 'features/maintenance', 'features/parts-inventory', 'features/locations',
      'features/analytics', 'features/statistics', 'features/scheduling', 'features/admin',
      'features/settings', 'features/power-monitors', 'features/profile', 'features/printer-groups',
    ];
    // Printer spool assignment is a safety-sensitive popover, not a migrated toolbar.
    const excluded = new Set([path.join(sourceRoot, 'features/printers/components/SlotPopover.tsx')]);
    const violations = directories.flatMap(directory => sourceFiles(path.join(sourceRoot, directory))).filter(file => !excluded.has(file)).flatMap(file =>
      findControlGlyphs(readFileSync(file, 'utf8')).map(hit => `${path.relative(sourceRoot, file)}:${hit}`));
    expect(violations).toEqual([]);
  });
});
