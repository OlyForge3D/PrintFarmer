import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const projectDirectory = fileURLToPath(new URL('..', import.meta.url));
const tscPath = resolve(projectDirectory, 'node_modules/typescript/bin/tsc');
const baselinePath = resolve(projectDirectory, 'scripts/test-typecheck-baseline.json');
const tscArguments = ['-p', 'tsconfig.test.json', '--noEmit', '--pretty', 'false'];

export function classifyDiagnostics(output, directory) {
  const lines = output.split(/\r?\n/).filter(Boolean);
  const fileDiagnostics = lines
    .map((line) => {
      const match = /^(?<path>.+?)\(\d+,\d+\): error TS\d+:/.exec(line);
      return match ? { line, path: match.groups.path } : undefined;
    })
    .filter(Boolean);

  return {
    fileDiagnostics,
    globalDiagnostics: lines.filter((line) => /^error TS\d+:/.test(line)),
    testDiagnostics: fileDiagnostics.filter(({ path }) => isTestFile(path, directory)),
  };
}

export function countTestFiles(listFilesOutput, directory) {
  return listFilesOutput
    .split(/\r?\n/)
    .filter((path) => isTestFile(path, directory))
    .length;
}

export function validateBaseline(baseline) {
  if (!Number.isInteger(baseline.testDiagnosticCount) || baseline.testDiagnosticCount < 0) {
    return 'testDiagnosticCount must be a non-negative integer.';
  }

  if (!Number.isInteger(baseline.minimumTestFileCount) || baseline.minimumTestFileCount < 1) {
    return 'minimumTestFileCount must be a positive integer.';
  }

  return undefined;
}

export function evaluate({ baseline, compilerResult, listFilesResult, output, listFilesOutput, directory }) {
  const baselineError = validateBaseline(baseline);
  if (baselineError) {
    return { ok: false, message: `Invalid test type-check baseline: ${baselineError}` };
  }

  if (compilerResult.error || compilerResult.signal || compilerResult.status === null) {
    return { ok: false, message: 'TypeScript test compiler did not complete successfully.' };
  }

  const diagnostics = classifyDiagnostics(output, directory);
  if (diagnostics.globalDiagnostics.length > 0) {
    return { ok: false, message: `TypeScript test compiler reported ${diagnostics.globalDiagnostics.length} global diagnostic(s).` };
  }

  if (compilerResult.status !== 0 && compilerResult.status !== 2) {
    return { ok: false, message: `TypeScript test compiler exited unexpectedly with status ${compilerResult.status}.` };
  }

  if (compilerResult.status !== 0 && diagnostics.fileDiagnostics.length === 0) {
    return { ok: false, message: 'TypeScript test compiler exited nonzero without file diagnostics.' };
  }

  if (listFilesResult.error || listFilesResult.signal || listFilesResult.status !== 0) {
    return { ok: false, message: 'TypeScript test compiler could not list its project files.' };
  }

  const testFileCount = countTestFiles(listFilesOutput, directory);
  if (testFileCount < baseline.minimumTestFileCount) {
    return { ok: false, message: `TypeScript test compiler found ${testFileCount} test file(s), below the ${baseline.minimumTestFileCount}-file floor.` };
  }

  if (diagnostics.testDiagnostics.length !== baseline.testDiagnosticCount) {
    const direction = diagnostics.testDiagnostics.length > baseline.testDiagnosticCount
      ? 'Fix the errors; do not raise the baseline.'
      : 'The baseline is stale; lower testDiagnosticCount to the current count.';
    return {
      ok: false,
      message: `Test type-check found ${diagnostics.testDiagnostics.length} test diagnostic(s); expected exactly ${baseline.testDiagnosticCount}. ${direction}`,
    };
  }

  return {
    ok: true,
    message: `Test type-check passed with ${diagnostics.testDiagnostics.length}/${baseline.testDiagnosticCount} baseline test diagnostic(s), ${diagnostics.fileDiagnostics.length - diagnostics.testDiagnostics.length} imported application diagnostic(s), and ${testFileCount} test file(s).`,
  };
}

function isTestFile(path, directory) {
  const normalized = relative(directory, resolve(directory, path)).replaceAll('\\', '/');
  return !normalized.startsWith('../')
    && !normalized.startsWith('node_modules/')
    && (normalized.startsWith('src/test/')
      || normalized.includes('/__tests__/')
      || /\.test\.(?:ts|tsx)$/.test(normalized));
}

function run() {
  const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
  const compilerResult = spawnSync(process.execPath, [tscPath, ...tscArguments], {
    cwd: projectDirectory,
    encoding: 'utf8',
  });
  const output = `${compilerResult.stdout}${compilerResult.stderr}`;
  process.stdout.write(output);

  const listFilesResult = spawnSync(process.execPath, [tscPath, ...tscArguments, '--listFilesOnly'], {
    cwd: projectDirectory,
    encoding: 'utf8',
  });
  const listFilesOutput = `${listFilesResult.stdout}${listFilesResult.stderr}`;
  const evaluation = evaluate({
    baseline,
    compilerResult,
    listFilesResult,
    output,
    listFilesOutput,
    directory: projectDirectory,
  });

  if (!evaluation.ok && listFilesOutput) {
    process.stdout.write(listFilesOutput);
  }

  if (!evaluation.ok) {
    throw new Error(evaluation.message);
  }

  console.warn(evaluation.message);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  run();
}
