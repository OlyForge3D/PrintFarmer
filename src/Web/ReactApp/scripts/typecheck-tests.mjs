import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { relative, resolve } from 'node:path';

const projectDirectory = fileURLToPath(new URL('..', import.meta.url));
const tscPath = resolve(projectDirectory, 'node_modules/typescript/bin/tsc');
const baselinePath = resolve(projectDirectory, 'scripts/test-typecheck-baseline.json');
const { testDiagnosticCount, minimumTestFileCount } = JSON.parse(readFileSync(baselinePath, 'utf8'));
const tscArguments = ['-p', 'tsconfig.test.json', '--noEmit', '--pretty', 'false'];
const result = spawnSync(process.execPath, [tscPath, ...tscArguments], {
  cwd: projectDirectory,
  encoding: 'utf8',
});

const output = `${result.stdout}${result.stderr}`;
const lines = output
  .split(/\r?\n/)
  .filter(Boolean);
const fileDiagnostics = lines
  .map((line) => {
    const match = /^(?<path>.+?)\(\d+,\d+\): error TS\d+:/.exec(line);
    return match ? { line, path: match.groups.path } : undefined;
  })
  .filter(Boolean);
const testDiagnostics = fileDiagnostics.filter(({ path }) => isTestFile(path));
const globalDiagnostics = lines.filter((line) => /^error TS\d+:/.test(line));

process.stdout.write(output);

if (result.error || result.signal || result.status === null) {
  fail('TypeScript test compiler did not complete successfully.');
}

if (globalDiagnostics.length > 0) {
  fail(`TypeScript test compiler reported ${globalDiagnostics.length} global diagnostic(s).`);
}

if (result.status !== 0 && fileDiagnostics.length === 0) {
  fail('TypeScript test compiler exited nonzero without file diagnostics.');
}

const listFilesResult = spawnSync(process.execPath, [tscPath, ...tscArguments, '--listFilesOnly'], {
  cwd: projectDirectory,
  encoding: 'utf8',
});
const listFilesOutput = `${listFilesResult.stdout}${listFilesResult.stderr}`;

if (listFilesResult.error || listFilesResult.signal || listFilesResult.status !== 0) {
  process.stdout.write(listFilesOutput);
  fail('TypeScript test compiler could not list its project files.');
}

const testFileCount = listFilesOutput
  .split(/\r?\n/)
  .filter((path) => isTestFile(path))
  .length;

if (testFileCount < minimumTestFileCount) {
  fail(`TypeScript test compiler found ${testFileCount} test file(s), below the ${minimumTestFileCount}-file floor.`);
}

if (testDiagnostics.length !== testDiagnosticCount) {
  const direction = testDiagnostics.length > testDiagnosticCount
    ? 'Fix the errors; do not raise the baseline.'
    : 'The baseline is stale; lower testDiagnosticCount to the current count.';
  fail(`Test type-check found ${testDiagnostics.length} test diagnostic(s); expected exactly ${testDiagnosticCount}. ${direction}`);
}

if (fileDiagnostics.length > 0 || result.status !== 0) {
  console.warn(`Test type-check passed with ${testDiagnostics.length}/${testDiagnosticCount} baseline test diagnostic(s), ${fileDiagnostics.length - testDiagnostics.length} imported application diagnostic(s), and ${testFileCount} test file(s).`);
}

function isTestFile(path) {
  const normalized = relative(projectDirectory, path).replaceAll('\\', '/');
  return normalized.startsWith('src/test/')
    || normalized.includes('/__tests__/')
    || /\.test\.(?:ts|tsx)$/.test(normalized);
}

function fail(message) {
  console.error(message);
  process.exitCode = 1;
}
