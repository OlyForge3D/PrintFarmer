import { describe, it, expect } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Token compliance regression tests.
 *
 * Scans critical UI component source files to ensure they do NOT contain
 * hardcoded Tailwind gray/slate/blue color classes. All color references
 * should use the pf-* design token system instead.
 *
 * This is a lint-like test that prevents design token regression.
 */

const REACT_APP_SRC = path.resolve(__dirname, '../../');

const CRITICAL_COMPONENTS: Record<string, string> = {
  PageTemplate: 'common/components/PageTemplate.tsx',
  Select: 'common/components/ui/Select.tsx',
  Button: 'common/components/ui/Button.tsx',
  Card: 'common/components/ui/Card.tsx',
  Badge: 'common/components/ui/Badge.tsx',
  Modal: 'common/components/modals/Modal.tsx',
  EmptyState: 'common/components/ui/EmptyState.tsx',
};

/**
 * Patterns that indicate hardcoded Tailwind color classes.
 * We match word-boundary class names like `gray-100`, `slate-500`, `blue-600`.
 * We exclude comment lines and known exceptions (e.g. CSS variable definitions).
 */
const FORBIDDEN_PATTERNS = [
  /\bgray-\d{2,3}\b/,
  /\bslate-\d{2,3}\b/,
  /\bblue-\d{2,3}\b/,
];

/**
 * Lines matching these patterns are allowed (false positive exclusions).
 * - CSS custom property definitions (--pf-*)
 * - Comment lines
 * - Import statements
 */
const EXCLUSION_PATTERNS = [
  /^\s*\/\//,     // single-line comments
  /^\s*\*/,       // block comment lines
  /--pf-/,        // CSS variable definitions
  /\/\*.*\*\//,   // inline block comments
];

function readComponentSource(relativePath: string): string {
  const fullPath = path.join(REACT_APP_SRC, relativePath);
  if (!fs.existsSync(fullPath)) {
    throw new Error(
      `Component file not found: ${fullPath}. ` +
      `If the component was moved, update CRITICAL_COMPONENTS in token-compliance.test.tsx.`,
    );
  }
  return fs.readFileSync(fullPath, 'utf-8');
}

function findForbiddenTokens(source: string): Array<{ line: number; text: string; pattern: string }> {
  const violations: Array<{ line: number; text: string; pattern: string }> = [];
  const lines = source.split('\n');

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // Skip excluded lines
    if (EXCLUSION_PATTERNS.some((p) => p.test(line))) {
      continue;
    }

    for (const pattern of FORBIDDEN_PATTERNS) {
      if (pattern.test(line)) {
        violations.push({
          line: i + 1,
          text: line.trim(),
          pattern: pattern.source,
        });
      }
    }
  }

  return violations;
}

describe('Design Token Compliance', () => {
  describe.each(Object.entries(CRITICAL_COMPONENTS))(
    '%s — no hardcoded color classes',
    (componentName, relativePath) => {
      it(`${componentName} uses pf-* tokens instead of gray/slate/blue classes`, () => {
        const source = readComponentSource(relativePath);
        const violations = findForbiddenTokens(source);

        if (violations.length > 0) {
          const report = violations
            .map((v) => `  Line ${v.line}: ${v.text} (matched: ${v.pattern})`)
            .join('\n');

          expect.fail(
            `${componentName} contains ${violations.length} hardcoded color class(es):\n${report}\n\n` +
            `Replace with pf-* design tokens (e.g., gray-500 → text-pf-text-secondary).`,
          );
        }

        // If we get here, no violations found
        expect(violations).toHaveLength(0);
      });
    },
  );

  it('all critical component files exist', () => {
    for (const [name, relativePath] of Object.entries(CRITICAL_COMPONENTS)) {
      const fullPath = path.join(REACT_APP_SRC, relativePath);
      expect(
        fs.existsSync(fullPath),
        `Missing component: ${name} at ${relativePath}`,
      ).toBe(true);
    }
  });

  it('has at least 6 critical components under scan', () => {
    expect(Object.keys(CRITICAL_COMPONENTS).length).toBeGreaterThanOrEqual(6);
  });
});
