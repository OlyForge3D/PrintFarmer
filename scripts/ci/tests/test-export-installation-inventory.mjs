import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const testDirectory = dirname(fileURLToPath(import.meta.url));
const root = resolve(testDirectory, '..', '..', '..');
const script = resolve(root, 'scripts', 'export-installation-inventory.ps1');
const fixture = resolve(testDirectory, 'fixtures', 'installation-inventory-import.json');
const missingInventoryFixture = resolve(testDirectory, 'fixtures', 'installation-inventory-missing-inventory.json');
const scalarInventoryFixture = resolve(testDirectory, 'fixtures', 'installation-inventory-scalar-inventory.json');
const powershell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
const quotePowerShell = (value) => value.replaceAll("'", "''");

function importSnapshot(input) {
  return execFileSync(
    powershell,
    [
      '-NoProfile',
      '-Command',
      `& '${quotePowerShell(script)}' -InputPath '${quotePowerShell(input)}' | ConvertTo-Json -Depth 20`,
    ],
    { encoding: 'utf8' },
  );
}

test('import marks nested inventory as imported and preserves evidence provenance', () => {
  const snapshot = JSON.parse(importSnapshot(fixture));
  const source = JSON.parse(readFileSync(fixture, 'utf8'));

  assert.equal(snapshot.inventory.snapshotOrigin, 'Imported');
  assert.equal(snapshot.inventory.snapshotSource, source.snapshotSource);
  assert.equal(snapshot.inventory.snapshotExportedAt, source.snapshotExportedAt);
  assert.equal(snapshot.inventory.services[0].observedAt, source.inventory.services[0].observedAt);
  assert.equal(snapshot.inventory.services[0].verifiedAt, source.inventory.services[0].verifiedAt);
  assert.equal(snapshot.inventory.eligibility, 'Unknown');
  assert.deepEqual(snapshot.inventory.eligibilityReasons, ['ImportedSnapshotIsNotLiveObservation']);
  assert.equal(snapshot.inventory.readiness, null);
});

test('export forwards the decrypted token as a bearer authorization value', () => {
  const output = resolve(testDirectory, '.installation-inventory-export-test.json');
  const command = `
function Invoke-RestMethod {
  param($Uri, $Headers, $Method)
  if ($Headers.Authorization -cne 'Bearer non-sensitive-test-token') { throw 'Bearer token was not forwarded.' }
  [pscustomobject]@{ inventory = [pscustomobject]@{ services = @() } }
}
$token = [System.Security.SecureString]::new()
'non-sensitive-test-token'.ToCharArray() | ForEach-Object { $token.AppendChar($_) }
$token.MakeReadOnly()
try {
  & '${quotePowerShell(script)}' -ApiBaseUri 'https://example.test' -AccessToken $token -OutputPath '${quotePowerShell(output)}'
}
finally {
  Remove-Item -LiteralPath '${quotePowerShell(output)}' -Force -ErrorAction SilentlyContinue
}
`;

  try {
    execFileSync(powershell, ['-NoProfile', '-Command', command], { encoding: 'utf8' });
  } finally {
    rmSync(output, { force: true });
  }

  assert.match(readFileSync(script, 'utf8'), /Authorization\s*=\s*"Bearer \$token"/);
});

test('import rejects envelopes with missing or scalar inventory properties', () => {
  for (const invalidFixture of [missingInventoryFixture, scalarInventoryFixture]) {
    assert.throws(
      () => importSnapshot(invalidFixture),
      /not a supported imported installation inventory snapshot/,
    );
  }
});
