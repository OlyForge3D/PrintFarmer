import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { render, screen } from '@testing-library/react';

import { Table, TableBody, TableRow } from '@/common/components/ui/Table';

/**
 * Row hover is an explicit affordance, not a default applied to every table.
 *
 * #1109 removes the global `table tbody tr:hover` rule and makes `TableRow`
 * opt in through `isHoverable`. #1088 still requires an opted-in row to
 * withhold its hover utility while selected, because both backgrounds live in
 * Tailwind's utility layer and the `:hover` selector would otherwise win.
 */

const HOVER_BACKGROUND = /(^|[\s:])hover:bg-/;

describe('table row hover behavior (#1088, #1109)', () => {
  describe('TableRow', () => {
    const renderRow = (isSelected: boolean, isHoverable?: boolean) =>
      render(
        <Table>
          <TableBody>
            <TableRow isSelected={isSelected} isHoverable={isHoverable}>
              <td>cell</td>
            </TableRow>
          </TableBody>
        </Table>,
      );

    it('withholds the hover background while selected', () => {
      renderRow(true, true);
      const row = screen.getByRole('row');

      expect(row.className).toContain('bg-pf-accent-bg/15');
      expect(row.className).not.toMatch(HOVER_BACKGROUND);
    });

    it('does not advertise interactivity by default', () => {
      renderRow(false);
      const row = screen.getByRole('row');

      expect(row.className).not.toMatch(HOVER_BACKGROUND);
    });

    it('offers a hover affordance when explicitly requested', () => {
      renderRow(false, true);
      const row = screen.getByRole('row');

      expect(row.className).toMatch(HOVER_BACKGROUND);
    });
  });

  describe('global table styles', () => {
    const controls = readFileSync(resolve(__dirname, '../../styles/controls.css'), 'utf8');
    const importMappingTable = readFileSync(
      resolve(__dirname, '../../features/slicer/components/import/ImportMappingTable.tsx'),
      'utf8',
    );

    it('does not hover every tbody row', () => {
      expect(controls).not.toMatch(/table\s+tbody\s+tr:hover\s*\{/);
    });

    it('only hovers the optional mapping row when it has a row action', () => {
      expect(importMappingTable).toContain("hasNote && 'cursor-pointer hover:bg-pf-bg-1'");
      expect(importMappingTable).not.toContain(
        "'bg-pf-bg-0 hover:bg-pf-bg-1 transition-colors'",
      );
    });
  });

  describe('call sites that pair a hover with a selected background', () => {
    const read = (relative: string) =>
      readFileSync(resolve(__dirname, '../..', relative), 'utf8');

    // Each entry is the exact ternary the row must use: selected background on
    // the true branch, hover on the false branch, never both at once.
    const sites: ReadonlyArray<[string, string]> = [
      [
        'features/fileBrowser/components/ExplorerView.tsx',
        "isSelected ? 'bg-pf-accent-bg/5' : 'hover:bg-pf-bg-2'",
      ],
      [
        'features/filamentManagement/components/OpenFilamentDbBrowserModal.tsx',
        "isSelected ? 'bg-pf-accent-bg/15' : 'hover:bg-pf-bg-2'",
      ],
      [
        'common/components/Table/SelectableRow.tsx',
        "isSelected ? 'bg-pf-bg-2' : 'hover:bg-pf-hover-overlay'",
      ],
      [
        'features/gcode/components/harvest/IndexedFilesList.tsx',
        "selected.has(file.id) ? 'bg-pf-bg-2' : 'hover:bg-pf-hover-overlay'",
      ],
    ];

    it.each(sites)('%s branches hover against selection', (file, ternary) => {
      expect(read(file)).toContain(ternary);
    });

    // Guards against someone re-adding a hover alongside the ternary rather
    // than in place of it. Scoped to the `<tr>` opening tag, because these
    // files legitimately hover buttons and breadcrumbs elsewhere.
    //
    // Scanned rather than matched with a regex. A `<tr ... >` tag holds JSX
    // expressions that contain both `>` (arrow functions) and quoted `>`, so no
    // regex delimits the tag correctly: a lazy `[\s\S]*?\n\s*>` starting at a
    // bare `<tr>` runs on until the next line-leading `>`, swallowing whatever
    // sits between. This walks the tag, tracking quotes and brace depth, and
    // stops at the first `>` outside both.
    const openingTags = (source: string, tag: string): string[] => {
      const found: string[] = [];
      const start = new RegExp(`<${tag}\\b`, 'g');

      for (const match of source.matchAll(start)) {
        let depth = 0;
        let quote = '';

        for (let i = match.index + match[0].length; i < source.length; i += 1) {
          const char = source[i];

          if (quote) {
            if (char === '\\') i += 1;
            else if (char === quote) quote = '';
            continue;
          }
          if (char === '"' || char === "'" || char === '`') quote = char;
          else if (char === '{') depth += 1;
          else if (char === '}') depth -= 1;
          else if (char === '>' && depth === 0) {
            found.push(source.slice(match.index, i + 1));
            break;
          }
        }
      }

      return found;
    };

    // Exact counts, not `> 0`. A scan that silently stops seeing a row would
    // stay green forever, which is the failure mode a style gate cannot afford.
    // Both files render one header row plus the selectable row; the filament
    // browser has a second header row for its grouped columns.
    const inlineRowSites: ReadonlyArray<[string, number]> = [
      ['features/fileBrowser/components/ExplorerView.tsx', 2],
      ['features/filamentManagement/components/OpenFilamentDbBrowserModal.tsx', 3],
    ];

    it.each(inlineRowSites)('%s declares no unconditional hover on the row', (file, expectedRows) => {
      const rowTags = openingTags(read(file), 'tr');
      expect(rowTags).toHaveLength(expectedRows);

      const offenders = rowTags.flatMap((tag) =>
        [...tag.matchAll(/hover:bg-[\w./[\]-]+/g)]
          // A hover that opens a quoted string immediately after `: ` is the
          // else branch of the selected ternary, which is the correct shape.
          .filter((match) => !/[:?]\s*[`'"]$/.test(tag.slice(0, match.index)))
          .map((match) => `${file}: ${match[0]}`),
      );

      expect(offenders).toEqual([]);
    });
  });
});

/**
 * The bulk tag modal painted its selected row `bg-pf-bg-2` onto a container
 * that was already `bg-pf-bg-2` — a literal zero-delta no-op (#1085). No
 * `--pf-bg-3` exists in any of the seven themes, and dropping the container to
 * `bg-pf-bg-1` would only have reached 1.05:1 in matrix, so the selected state
 * is now carried by an accent tint plus an accent ring. The ring measures
 * 4.33:1 (light) to 13.93:1 (matrix) against the container and 3.58:1 to
 * 10.92:1 against the tinted row, clearing SC 1.4.11 in every theme.
 */
describe('BulkTagAssignmentModal selected row is visible against its container (#1085)', () => {
  const source = readFileSync(
    resolve(__dirname, '../../common/components/modals/BulkTagAssignmentModal.tsx'),
    'utf8',
  );

  const containerClass = source.match(/className="(bg-pf-bg-2[^"]*max-h-48[^"]*)"/)?.[1];
  const selectedBranch = source.match(/\?\s*'([^']*ring-pf-accent[^']*)'/)?.[1];

  it('renders the model list on a known container surface', () => {
    expect(containerClass).toBeDefined();
    expect(containerClass).toContain('bg-pf-bg-2');
  });

  it('does not paint the selected row in the container surface colour', () => {
    expect(selectedBranch).toBeDefined();
    expect(selectedBranch).not.toMatch(/(^|\s)bg-pf-bg-2(\s|$)/);
  });

  it('carries the selected state on an accent tint and an accent ring', () => {
    expect(selectedBranch).toContain('bg-pf-accent-bg/15');
    expect(selectedBranch).toContain('ring-1');
    expect(selectedBranch).toContain('ring-inset');
    expect(selectedBranch).toContain('ring-pf-accent');
  });

  it('keeps the secondary label above AA on the tinted row', () => {
    // `text-pf-text-tertiary` was already below 4.5:1 on `bg-pf-bg-2` (3.60:1
    // dark, 3.98:1 light, 4.40:1 blueprint) and the tint would push it to ~3.1.
    expect(source).not.toContain('text-pf-text-tertiary');
    expect(source).toContain('text-sm text-pf-text-secondary');
  });
});
