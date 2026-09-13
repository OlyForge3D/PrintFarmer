import { resolve } from 'node:path';
import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';

const root = resolve(import.meta.dirname, '..');
const eslint = new ESLint({ cwd: root, overrideConfigFile: resolve(root, 'eslint.config.js') });

async function restrictedProperties(code, filePath) {
  const [result] = await eslint.lintText(code, { filePath });
  expect(result.fatalErrorCount).toBe(0);
  return result.messages.filter(message => message.ruleId === 'no-restricted-properties');
}

describe('shared UUID helper lint policy', () => {
  it.each([
    'export const id = crypto.randomUUID();',
    'export const id = window.crypto.randomUUID();',
    'export const id = globalThis.crypto.randomUUID();',
    "export const id = crypto['randomUUID']();",
    'export const id = crypto.randomUUID?.();',
    'export const { randomUUID } = crypto;',
    'export const makeId = crypto.randomUUID;',
  ])('rejects direct randomUUID access: %s', async code => {
    const messages = await restrictedProperties(code, 'src/services/uuid-policy-example.ts');
    expect(messages).toHaveLength(1);
    expect(messages[0]).toMatchObject({
      severity: 2,
      message: expect.stringContaining('Use generateUUID from @/utils/uuid'),
    });
  });

  it('applies the same restriction to TSX components', async () => {
    expect(await restrictedProperties(
      'export const id = crypto.randomUUID();',
      'src/components/UuidPolicyExample.tsx',
    )).toHaveLength(1);
  });

  it('allows the shared helper and unrelated ID APIs in production', async () => {
    expect(await restrictedProperties(
      "import { generateUUID } from '@/utils/uuid'; export const id = generateUUID();",
      'src/services/uuid-policy-example.ts',
    )).toEqual([]);
    expect(await restrictedProperties(
      "import { useId } from 'react'; export function useFieldId() { return useId(); }",
      'src/common/hooks/useFieldId.ts',
    )).toEqual([]);
  });

  it.each([
    'src/utils/uuid.ts',
    'src/utils/__tests__/uuid.test.ts',
    'src/test/uuid-fixture.ts',
    'src/services/uuid-policy-example.test.ts',
    'src/components/UuidPolicyExample.test.tsx',
    'src/services/uuid-policy-example.spec.ts',
    'e2e/uuid-policy-example.spec.ts',
  ])('permits native UUID access in the helper or test code: %s', async filePath => {
    expect(await restrictedProperties('export const id = crypto.randomUUID();', filePath)).toEqual([]);
  });
});
