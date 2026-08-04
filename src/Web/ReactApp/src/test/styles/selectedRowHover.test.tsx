import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { render, screen } from '@testing-library/react';

import { Table, TableBody, TableRow } from '@/common/components/ui/Table';

/**
 * A selected row must keep its highlight while the pointer is over it.
 *
 * #1088 blamed the global `table tbody tr:hover` rule in `controls.css` on a
 * specificity reading — (0,1,3) against a selected `.bg-pf-bg-2` at (0,1,0).
 * That premise is false. The global rule sits in `@layer components` and every
 * Tailwind utility sits in `@layer utilities`, and layer order beats
 * specificity, so the global rule cannot displace a selected background.
 * Verified two ways: in the built stylesheet the rule falls inside the
 * `@layer components` block while `.bg-pf-bg-2` falls inside the later
 * `@layer utilities` block (re-derive offsets per build — the asset hash and
 * the byte positions both move); and in chromium with transitions suppressed,
 * `<tr class="bg-pf-bg-2">` reads an identical computed background at rest and
 * on hover in all seven themes.
 *
 * What did erase the highlight was each row's OWN unconditional `hover:bg-*`
 * utility. That lands in the same layer as the selected class, where `:hover`
 * at (0,1,1) does outrank a plain class at (0,1,0). Chromium confirmed it:
 * `bg-pf-accent-bg/15 hover:bg-pf-bg-1` and `bg-pf-accent-bg/5 hover:bg-pf-bg-2`
 * both changed background on hover, in all seven themes.
 *
 * So the invariant this file guards is: a selectable row never carries a
 * background hover utility while it is selected. jsdom does not load the
 * stylesheet and cannot evaluate the cascade, so the render assertions check
 * the emitted class list and the source assertions check the branch shape at
 * the three call sites that are too expensive to mount. The cascade reasoning
 * that makes this invariant sufficient is the browser measurement above.
 */

const HOVER_BACKGROUND = /(^|[\s:])hover:bg-/;

describe('selected rows keep their highlight under the pointer (#1088)', () => {
  describe('TableRow', () => {
    const renderRow = (isSelected: boolean) =>
      render(
        <Table>
          <TableBody>
            <TableRow isSelected={isSelected}>
              <td>cell</td>
            </TableRow>
          </TableBody>
        </Table>,
      );

    it('withholds the hover background while selected', () => {
      renderRow(true);
      const row = screen.getByRole('row');

      expect(row.className).toContain('bg-pf-accent-bg/15');
      expect(row.className).not.toMatch(HOVER_BACKGROUND);
    });

    it('still offers a hover affordance when not selected', () => {
      renderRow(false);
      const row = screen.getByRole('row');

      expect(row.className).toMatch(HOVER_BACKGROUND);
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
    const inlineRowSites = [
      'features/fileBrowser/components/ExplorerView.tsx',
      'features/filamentManagement/components/OpenFilamentDbBrowserModal.tsx',
    ] as const;

    it.each(inlineRowSites)('%s declares no unconditional hover on the row', (file) => {
      const rowTags = [...read(file).matchAll(/<tr\b[\s\S]*?\n\s*>/g)].map((match) => match[0]);
      expect(rowTags.length).toBeGreaterThan(0);

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
