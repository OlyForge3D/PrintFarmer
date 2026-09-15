import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const root = resolve(testDirectory, '..', '..', '..');
const script = resolve(root, 'scripts', 'export-installation-inventory.ps1');
const fixture = resolve(testDirectory, 'fixtures', 'installation-inventory-import.json');
const powershell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
const quotePowerShell = (value) => value.replaceAll("'", "''");

test('import marks nested inventory as imported and preserves evidence provenance', () => {
  const output = execFileSync(
    powershell,
    [
      '-NoProfile',
      '-Command',
      `& '${quotePowerShell(script)}' -InputPath '${quotePowerShell(fixture)}' | ConvertTo-Json -Depth 20`,
    ],
    { encoding: 'utf8' },
  );
  const snapshot = JSON.parse(output);
  const source = JSON.parse(readFileSync(fixture, 'utf8'));

  assert.equal(snapshot.inventory.snapshotOrigin, 'Imported');
  assert.equal(snapshot.inventory.snapshotSource, source.snapshotSource);
  assert.equal(snapshot.inventory.snapshotExportedAt, source.snapshotExportedAt);
  assert.equal(snapshot.inventory.services[0].observedAt, source.inventory.services[0].observedAt);
  assert.equal(snapshot.inventory.services[0].verifiedAt, source.inventory.services[0].verifiedAt);
});

test('export constructs and clears the bearer authorization header', () => {
  const source = readFileSync(script, 'utf8');

  assert.match(source, /\$headers = @\{ Authorization = "Bearer \$token" \}/);
  assert.match(source, /finally\s*\{\s*\$headers = \$null\s*\$token = \$null\s*\}/);
});
