import { isAbsolute, relative, resolve } from "node:path";

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
    testDiagnostics: fileDiagnostics.filter(({ path }) =>
      isTestFile(path, directory),
    ),
  };
}

export function countTestFiles(listFilesOutput, directory) {
  return listFilesOutput
    .split(/\r?\n/)
    .filter((path) => isTestFile(path, directory)).length;
}

export function validateBaseline(baseline) {
  if (!baseline || typeof baseline !== "object") {
    return "baseline must be an object.";
  }

  if (
    !Number.isInteger(baseline.testDiagnosticCount) ||
    baseline.testDiagnosticCount < 0
  ) {
    return "testDiagnosticCount must be a non-negative integer.";
  }

  if (
    !Number.isInteger(baseline.minimumTestFileCount) ||
    baseline.minimumTestFileCount < 1
  ) {
    return "minimumTestFileCount must be a positive integer.";
  }

  return undefined;
}

export function evaluate({
  baseline,
  compilerResult,
  listFilesResult,
  output,
  listFilesOutput,
  directory,
}) {
  const baselineError = validateBaseline(baseline);
  if (baselineError) {
    return {
      ok: false,
      message: `Invalid test type-check baseline: ${baselineError}`,
      showListFilesOutput: false,
    };
  }

  if (
    compilerResult.error ||
    compilerResult.signal ||
    compilerResult.status === null
  ) {
    return {
      ok: false,
      message: "TypeScript test compiler did not complete successfully.",
      showListFilesOutput: false,
    };
  }

  const diagnostics = classifyDiagnostics(output, directory);
  if (diagnostics.globalDiagnostics.length > 0) {
    return {
      ok: false,
      message: `TypeScript test compiler reported ${diagnostics.globalDiagnostics.length} global diagnostic(s).`,
      showListFilesOutput: false,
    };
  }

  // TypeScript 5.9's emitFilesAndReportErrorsAndGetExitStatus returns 2 for
  // --noEmit without outFile; status 1 means an emit was skipped, which this
  // invocation cannot legitimately produce.
  if (compilerResult.status !== 0 && compilerResult.status !== 2) {
    return {
      ok: false,
      message: `TypeScript test compiler exited unexpectedly with status ${compilerResult.status}.`,
      showListFilesOutput: false,
    };
  }

  if (compilerResult.status !== 0 && diagnostics.fileDiagnostics.length === 0) {
    return {
      ok: false,
      message:
        "TypeScript test compiler exited nonzero without file diagnostics.",
      showListFilesOutput: false,
    };
  }

  if (
    listFilesResult.error ||
    listFilesResult.signal ||
    listFilesResult.status !== 0
  ) {
    return {
      ok: false,
      message: "TypeScript test compiler could not list its project files.",
      showListFilesOutput: true,
    };
  }

  const testFileCount = countTestFiles(listFilesOutput, directory);
  if (testFileCount < baseline.minimumTestFileCount) {
    return {
      ok: false,
      message: `TypeScript test compiler found ${testFileCount} test file(s); expected at least ${baseline.minimumTestFileCount}. Regenerate minimumTestFileCount in scripts/test-typecheck-baseline.json in the same commit.`,
      showListFilesOutput: true,
    };
  }

  if (diagnostics.testDiagnostics.length !== baseline.testDiagnosticCount) {
    const direction =
      diagnostics.testDiagnostics.length > baseline.testDiagnosticCount
        ? "Fix the errors; do not raise the exact snapshot."
        : "The exact snapshot is stale; regenerate testDiagnosticCount in scripts/test-typecheck-baseline.json in the same commit.";
    return {
      ok: false,
      message: `Test type-check measured ${diagnostics.testDiagnostics.length} direct test diagnostic(s); expected exact snapshot ${baseline.testDiagnosticCount}. ${direction}`,
      showListFilesOutput: false,
    };
  }

  return {
    ok: true,
    message: `Test type-check passed with ${diagnostics.testDiagnostics.length}/${baseline.testDiagnosticCount} baseline test diagnostic(s), ${diagnostics.fileDiagnostics.length - diagnostics.testDiagnostics.length} imported application diagnostic(s), and ${testFileCount} test file(s).`,
    showListFilesOutput: false,
  };
}

export function isTestFile(path, directory) {
  const normalized = relative(directory, resolve(directory, path)).replaceAll(
    "\\",
    "/",
  );
  return (
    !isAbsolute(normalized) &&
    !normalized.startsWith("../") &&
    !normalized.startsWith("node_modules/") &&
    (normalized.startsWith("src/test/") ||
      normalized.includes("/__tests__/") ||
      /\.test\.(?:ts|tsx)$/.test(normalized))
  );
}
