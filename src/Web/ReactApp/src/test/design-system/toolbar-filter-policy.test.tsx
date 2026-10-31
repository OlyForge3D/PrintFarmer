import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

const sourceRoot = path.resolve(__dirname, '../..');
const featuresRoot = path.join(sourceRoot, 'features');
const policyDocument = readFileSync(
  path.join(sourceRoot, 'design-system/DESIGN-LANGUAGE.md'),
  'utf8',
);

const exceptions = {
  'features/admin/components/SystemLogsContent.tsx': {
    fields: ['CorrelationId', 'Level', 'From', 'To', 'Metadata'],
    reason: 'Primary simple-query fields; controls the active query mode and export.',
  },
  'features/admin/pages/LoginAuditPage.tsx': {
    fields: ['Username', 'Rows per page'],
    reason: 'Primary username search and result page-size controls; date and status refinements use FiltersDisclosure.',
  },
  'features/filamentManagement/components/FilamentsTab.tsx': {
    fields: ['Search', 'Sort', 'Show'],
    reason: 'Primary search, result ordering and page-size controls.',
  },
  'features/filamentManagement/components/SpoolsTab.tsx': {
    fields: ['Search', 'Sort'],
    reason: 'Primary search and result-ordering controls.',
  },
  'features/queue/components/QueueDateRangeBar.tsx': {
    fields: ['From', 'To'],
    reason: "Primary range controls inside the date-range preset's own expander.",
  },
  'features/printers/components/PrinterFilesModal.tsx': {
    fields: ['Sort by'],
    reason: 'Sorting changes result order, not the result set.',
  },
  'features/auto-dispatch/pages/AutoDispatchDashboardPage.tsx': {
    fields: ['Sort by'],
    reason: 'Sorting changes result order, not the result set.',
  },
  'features/printers/pages/PrintersPage.tsx': {
    fields: ['Sort by'],
    reason: 'Sorting changes result order, not the result set.',
  },
  'features/queue/components/HistoryFiltersBar.tsx': {
    fields: ['Sort by'],
    reason: 'Sorting changes result order, not the result set.',
  },
  'features/queue/components/QueueFiltersBar.tsx': {
    fields: ['Sort by'],
    reason: 'Sorting changes result order, not the result set.',
  },
  'features/fileBrowser/components/FileBrowserToolbar.tsx': {
    fields: ['Search', 'Sort'],
    reason: 'Primary search and sorting controls for file browsing.',
  },
  'features/fileBrowser/components/FileBrowser.tsx': {
    fields: ['Folders'],
    reason: 'Folder navigation toggle; not a result-set filter.',
  },
  'features/files/components/LocalLibraryFolders.tsx': {
    fields: ['Folder'],
    reason: 'Folder navigation/selection/action controls.',
  },
  'features/files/components/PrinterFileCatalog.tsx': {
    fields: ['Search'],
    reason: 'Primary search query for the printer-file catalog.',
  },
  'features/files/pages/FilesPage.tsx': {
    fields: ['Type'],
    reason: 'Type filter is declared separately for page composition and rendered inside the shared disclosure.',
  },
} as const;

type ToolbarFieldUse = {
  label: string;
  insideDisclosure: boolean;
};

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    if (entry.name === '__tests__') return [];
    const file = path.join(directory, entry.name);
    return entry.isDirectory() ? sourceFiles(file) : file.endsWith('.tsx') ? [file] : [];
  });
}

function stringAttribute(element: ts.JsxOpeningLikeElement, name: string): string | undefined {
  const attribute = element.attributes.properties.find(
    (property): property is ts.JsxAttribute =>
      ts.isJsxAttribute(property) && property.name.getText() === name,
  );
  const initializer = attribute?.initializer;
  return initializer && (ts.isStringLiteral(initializer) || ts.isNoSubstitutionTemplateLiteral(initializer))
    ? initializer.text
    : undefined;
}

function toolbarControlUses(source: string): ToolbarFieldUse[] {
  if (!source.includes('ToolbarField') && !(source.includes('Toolbar') && source.includes('Select'))) {
    return [];
  }
  const file = ts.createSourceFile('feature.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const uses: ToolbarFieldUse[] = [];

  function visit(node: ts.Node, insideDisclosure: boolean, insideToolbar: boolean, insideField: boolean): void {
    const inDisclosure = insideDisclosure || (
      ts.isJsxElement(node) && node.openingElement.tagName.getText(file) === 'FiltersDisclosure'
    );
    const tag = ts.isJsxElement(node) ? node.openingElement.tagName.getText(file) : undefined;
    const inToolbar = insideToolbar || tag === 'Toolbar' || tag === 'Toolbar.Group';
    const inField = insideField || tag === 'ToolbarField';

    if (
      (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node))
      && (
        node.tagName.getText(file) === 'ToolbarField'
        || (node.tagName.getText(file) === 'Select' && inToolbar && !inField)
      )
    ) {
      uses.push({
        label: stringAttribute(node, 'label') ?? stringAttribute(node, 'aria-label')
          ?? stringAttribute(node, 'htmlFor') ?? stringAttribute(node, 'id') ?? '<dynamic>',
        insideDisclosure: inDisclosure,
      });
    }

    ts.forEachChild(node, (child) => visit(child, inDisclosure, inToolbar, inField));
  }

  visit(file, false, false, false);
  return uses;
}

function filesTypeFieldIsRenderedInsideDisclosure(source: string): boolean {
  const file = ts.createSourceFile('FilesPage.tsx', source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  let declaredWithToolbarField = false;
  let renderedInsideDisclosure = false;

  function containsToolbarField(node: ts.Node): boolean {
    if (
      (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node))
      && node.tagName.getText(file) === 'ToolbarField'
    ) {
      return true;
    }
    return node.forEachChild(containsToolbarField) ?? false;
  }

  function visit(node: ts.Node, insideDisclosure: boolean): void {
    const inDisclosure = insideDisclosure || (
      ts.isJsxElement(node) && node.openingElement.tagName.getText(file) === 'FiltersDisclosure'
    );

    if (
      ts.isVariableDeclaration(node)
      && ts.isIdentifier(node.name)
      && node.name.text === 'fileTypeFilter'
    ) {
      declaredWithToolbarField = !!node.initializer && containsToolbarField(node.initializer);
    }
    if (
      inDisclosure
      && ts.isJsxExpression(node)
      && node.expression
      && ts.isIdentifier(node.expression)
      && node.expression.text === 'fileTypeFilter'
    ) {
      renderedInsideDisclosure = true;
    }

    ts.forEachChild(node, (child) => visit(child, inDisclosure));
  }

  visit(file, false);
  return declaredWithToolbarField && renderedInsideDisclosure;
}

describe('Toolbar filter disclosure policy', () => {
  it('classifies every feature ToolbarField and direct toolbar Select as a disclosure refinement or an exact exception', () => {
    const files = sourceFiles(featuresRoot);
    const allUses = files.flatMap((file) => {
      const relativePath = path.relative(sourceRoot, file).split(path.sep).join('/');
      return toolbarControlUses(readFileSync(file, 'utf8')).map((use) => ({ ...use, file: relativePath }));
    });
    const unwrapped = allUses.filter((use) => !use.insideDisclosure);
    const actualExceptions = new Map<string, string[]>();

    for (const use of unwrapped) {
      const fields = actualExceptions.get(use.file) ?? [];
      fields.push(use.label);
      actualExceptions.set(use.file, fields);
    }

    expect(allUses.length, 'the source scan must discover the feature toolbar fields').toBeGreaterThan(30);
    expect([...actualExceptions.keys()].sort()).toEqual(Object.keys(exceptions).sort());

    for (const [file, exception] of Object.entries(exceptions)) {
      expect(actualExceptions.get(file)?.sort(), `${file} unwrapped fields`).toEqual([...exception.fields].sort());
      const policyRow = policyDocument.split('\n').find((line) => line.includes(`\`${file}\``));
      expect(policyRow, `${file} must have a documented owner and reason`).toBeDefined();
      expect(policyRow).toContain(exception.fields.map((field) => `\`${field}\``).join(', '));
      expect(policyRow).toContain(exception.reason);
    }
  });

  it('keeps the extracted Files Type field rendered by the FiltersDisclosure', () => {
    const filesPage = readFileSync(
      path.join(featuresRoot, 'files/pages/FilesPage.tsx'),
      'utf8',
    );
    expect(filesTypeFieldIsRenderedInsideDisclosure(filesPage)).toBe(true);
  });

  it('distinguishes a disclosed refinement from a primary unwrapped field', () => {
    const source = `
      <FiltersDisclosure>
        <ToolbarField label="Material" />
      </FiltersDisclosure>
      <ToolbarField label="Search" />
    `;
    expect(toolbarControlUses(source)).toEqual([
      { label: 'Material', insideDisclosure: true },
      { label: 'Search', insideDisclosure: false },
    ]);
  });

  it('catches a direct toolbar Select outside the disclosure without double-counting labelled fields', () => {
    const source = `
      <Toolbar>
        <Select aria-label="Material" />
        <FiltersDisclosure>
          <Select aria-label="Manufacturer" />
          <ToolbarField label="Status"><Select aria-label="Status" /></ToolbarField>
        </FiltersDisclosure>
      </Toolbar>
      <Select aria-label="Unrelated form" />
    `;
    const uses = toolbarControlUses(source);
    expect(uses).toEqual([
      { label: 'Material', insideDisclosure: false },
      { label: 'Manufacturer', insideDisclosure: true },
      { label: 'Status', insideDisclosure: true },
    ]);
    expect(uses.filter((use) => !use.insideDisclosure)).toEqual([
      { label: 'Material', insideDisclosure: false },
    ]);
  });
});
