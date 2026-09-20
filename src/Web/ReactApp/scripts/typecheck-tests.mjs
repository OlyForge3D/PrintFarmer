import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const projectDirectory = process.cwd();
const tscPath = resolve(projectDirectory, 'node_modules/typescript/bin/tsc');
const baselinePath = resolve(projectDirectory, 'scripts/test-typecheck-baseline.json');
const { testDiagnosticCount } = JSON.parse(readFileSync(baselinePath, 'utf8'));
const result = spawnSync(process.execPath, [tscPath, '-p', 'tsconfig.test.json', '--noEmit', '--pretty', 'false'], {
  cwd: projectDirectory,
  encoding: 'utf8',
});

const output = `${result.stdout}${result.stderr}`;
const diagnostics = output
  .split(/\r?\n/)
  .filter((line) => /(?:^|[/\\])(src[/\\](?:test[/\\]|.*(?:__tests__[/\\]|\.test\.)))/.test(line) && /: error TS\d+:/.test(line));

process.stdout.write(output);

if (result.error) {
  throw result.error;
}

if (diagnostics.length > testDiagnosticCount) {
  console.error(`Test type-check failed: ${diagnostics.length - testDiagnosticCount} new diagnostic(s) above the ${testDiagnosticCount} diagnostic baseline.`);
  process.exitCode = 1;
} else if (diagnostics.length > 0 || result.status !== 0) {
  const sourceDiagnostics = output.split(/\r?\n/).filter((line) => /: error TS\d+:/.test(line)).length;
  console.warn(`Test type-check passed with ${diagnostics.length}/${testDiagnosticCount} baseline test diagnostic(s) and ${sourceDiagnostics - diagnostics.length} imported application diagnostic(s).`);
}
