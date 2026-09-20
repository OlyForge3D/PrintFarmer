import { readFileSync, realpathSync } from "node:fs";
import { isAbsolute, relative, resolve } from "node:path";

// Matches TypeScript's own convention for the directive: a line-comment
// containing "@ts-nocheck" anywhere before the first real statement. This is
// intentionally permissive (it does not require the directive to be the very
// first token in the file) so a file cannot dodge detection with leading
// blank lines or an extra comment.
const TS_NOCHECK_PATTERN = /^[ \t]*\/\/[ \t]*@ts-nocheck\b/m;

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
    // Deliberately broad (directory-based): any diagnostic under a test root
    // must be accounted for here, even in a helper/fixture file that does not
    // itself count toward the file-count floor below. A file that escapes
    // this bucket by moving outside src/test/ and __tests__/ lands in
    // ordinary application source instead, where `npm run typecheck:app`
    // (see #2806) picks it up — the two gates are meant to jointly cover all
    // of src/ with no unowned gap in between.
    testDiagnostics: fileDiagnostics.filter(({ path }) =>
      isTestFile(path, directory),
    ),
  };
}

function normalizePath(path, directory) {
  return relative(directory, resolve(directory, path)).replaceAll("\\", "/");
}

// Stricter than isTestFile: only files matching the real test-naming
// convention count toward the file-count floor, so a helper, fixture, or
// stub dropped under src/test/ or __tests__/ cannot satisfy it without
// itself being a test. isTestFile stays broad because the diagnostic bucket
// above must still catch bugs in those same helper files.
export function isCountableTestFile(path, directory) {
  return (
    isTestFile(path, directory) &&
    /\.test\.(?:ts|tsx)$/.test(normalizePath(path, directory))
  );
}

function hasTsNoCheckDirective(absolutePath) {
  let content;
  try {
    content = readFileSync(absolutePath, "utf8");
  } catch {
    // Files that cannot be read off disk (e.g. synthetic paths used only in
    // unit tests) are treated as not opted out, matching prior behavior.
    return false;
  }
  return TS_NOCHECK_PATTERN.test(content);
}

function toRealPath(absolutePath) {
  try {
    return realpathSync(absolutePath);
  } catch {
    return absolutePath;
  }
}

export function countTestFiles(listFilesOutput, directory) {
  const seen = new Set();

  for (const path of listFilesOutput.split(/\r?\n/)) {
    if (!path || !isCountableTestFile(path, directory)) {
      continue;
    }

    const absolute = resolve(directory, path);
    // A file with `// @ts-nocheck` raises the count while contributing zero
    // diagnostics, decoupling the file-count floor from real coverage; it
    // must not satisfy the floor.
    if (hasTsNoCheckDirective(absolute)) {
      continue;
    }

    // Realpath-normalize so a symlink to an already-counted file cannot
    // inflate the count a second time.
    seen.add(toRealPath(absolute));
  }

  return seen.size;
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
  // Collect every gate failure before returning so an edit that trips both
  // the file-count floor and the diagnostic snapshot in the same run reports
  // as one failure, not two sequential, seemingly-unrelated ones.
  const failures = [];
  let showListFilesOutput = false;

  if (testFileCount < baseline.minimumTestFileCount) {
    failures.push(
      `TypeScript test compiler found ${testFileCount} test file(s); expected at least ${baseline.minimumTestFileCount}. Regenerate minimumTestFileCount in scripts/test-typecheck-baseline.json in the same commit.`,
    );
    showListFilesOutput = true;
  }

  if (diagnostics.testDiagnostics.length !== baseline.testDiagnosticCount) {
    const direction =
      diagnostics.testDiagnostics.length > baseline.testDiagnosticCount
        ? "Fix the errors; do not raise the exact snapshot."
        : "The exact snapshot is stale; regenerate testDiagnosticCount in scripts/test-typecheck-baseline.json in the same commit.";
    failures.push(
      `Test type-check measured ${diagnostics.testDiagnostics.length} direct test diagnostic(s); expected exact snapshot ${baseline.testDiagnosticCount}. ${direction}`,
    );
  }

  if (failures.length > 0) {
    return {
      ok: false,
      message: failures.join("\n"),
      showListFilesOutput,
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
