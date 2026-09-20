import { readFileSync, realpathSync } from "node:fs";
import { isAbsolute, relative, resolve } from "node:path";

// Matches TypeScript's own convention for the directive: a line-comment
// containing "@ts-nocheck" anywhere before the first real statement. This is
// intentionally permissive (it does not require the directive to be the very
// first token in the file) so a file cannot dodge detection with leading
// blank lines or an extra comment. `\/\/+` (one or more slashes) is
// deliberate, not `\/\/\/?`: tsc honors both `//` and `///` (verified against
// the pinned compiler), and a triple-slash-only pattern would still miss a
// four-slash comment, so this accepts any run of slashes rather than
// enumerating specific counts. The block-comment form `/* @ts-nocheck */` is
// NOT matched here (and must not be) because tsc does not honor it either
// (verified: still 1 diagnostic) -- matching it would exclude a file that is
// actually still type-checked.
const TS_NOCHECK_PATTERN = /^[ \t]*\/\/+[ \t]*@ts-nocheck\b/m;

// Bounds an env-var-supplied override for a spawnSync limit so the seam can
// only ever tighten the default (shorten the timeout), never lengthen,
// enlarge, or disable it. Invalid, non-finite, and oversize values must
// silently fall back to the default.
export function clampedOverride(envValue, defaultValue) {
  const parsed = Number(envValue);
  return Number.isSafeInteger(parsed) && parsed > 0 && parsed <= defaultValue
    ? parsed
    : defaultValue;
}

export function formatSinkOutput(stdout, stderr) {
  return `${stdout ?? ""}${stderr ?? ""}`;
}

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
    // this bucket by moving outside src/test/ and __tests__/ (and does not
    // itself end in .test.ts/.test.tsx, which isTestFile also matches
    // anywhere in src/) lands in ordinary application source instead, where
    // `npm run typecheck:app` (see #2806) picks it up. This coverage is NOT
    // total: `src/**/*.spec.*` is excluded by tsconfig.app.json and never
    // included by tsconfig.test.json, so a `*.spec.ts` file is owned by
    // neither gate today. That gap is latent (zero such files currently
    // exist under src/, pinned by the guard test below) and tracked, not
    // closed here -- do not read this comment as "no gap exists."
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

export function hasTsNoCheckDirective(absolutePath) {
  let content;
  try {
    content = readFileSync(absolutePath, "utf8");
  } catch (error) {
    // Only ENOENT (a synthetic path used purely in unit tests) is treated as
    // "not opted out" -- matching prior behavior for paths that never existed
    // on disk. Every other I/O failure (EACCES, EPERM, EBUSY, ...) is
    // unexpected for a path tsc itself just listed via --listFilesOnly, so it
    // must fail closed (surface the error) rather than silently swallow it
    // and treat the file as checked when we could not actually read it.
    if (error && error.code === "ENOENT") {
      return false;
    }
    throw error;
  }
  // Node's utf8 decoding does not strip a byte-order mark, so a BOM-prefixed
  // file would otherwise shift the pattern's `^` anchor past the directive
  // and evade detection entirely.
  return TS_NOCHECK_PATTERN.test(content.replace(/^\uFEFF/, ""));
}

// Exported for reuse by scripts/typecheck-app-core.mjs (#2811 item 5 / R2:
// the application ratchet must detect the same evasion, not a second
// hand-rolled regex that can drift from this one).
//
// `platform` and `realpathImpl` are injectable seams (R8): the win32
// casefold contract -- "prefer .native, then casefold the result on win32" --
// must be exercisable deterministically on any host OS, not only pinned by a
// real-filesystem test that CI (ubuntu-latest/macos-latest only, no Windows
// runner) never executes. Defaults preserve production behavior exactly.
export function toRealPath(
  absolutePath,
  { platform = process.platform, realpathImpl = realpathSync } = {},
) {
  // realpathImpl.native calls the OS syscall directly and resolves the true
  // on-disk casing on case-insensitive filesystems (verified on Windows:
  // realpathSync alone kept two differently-cased paths to the same file
  // distinct, while realpathSync.native collapsed them to one canonical
  // string). Fall back to the JS implementation when .native is unavailable.
  const resolveRealPath = realpathImpl.native ?? realpathImpl;
  let real;
  try {
    real = resolveRealPath(absolutePath);
  } catch {
    real = absolutePath;
  }
  // Belt-and-suspenders: also casefold explicitly on win32 rather than
  // relying solely on .native's canonicalization, since filesystem behavior
  // (NTFS vs. exFAT, network shares, etc.) can vary. POSIX filesystems are
  // case-sensitive by design, so casefolding there would be incorrect.
  return platform === "win32" ? real.toLowerCase() : real;
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

  if (compilerResult.error?.code === "ETIMEDOUT") {
    return {
      ok: false,
      message: "TypeScript test compiler timed out and was killed.",
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

  if (listFilesResult.error?.code === "ETIMEDOUT") {
    return {
      ok: false,
      message: "TypeScript test compiler timed out and was killed.",
      showListFilesOutput: true,
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
  // the file-count floor and the diagnostic count in the same run reports as
  // one failure, not two sequential, seemingly-unrelated ones.
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
        ? "Fix the errors; do not raise the exact count."
        : "The exact count is stale; regenerate testDiagnosticCount in scripts/test-typecheck-baseline.json in the same commit.";
    failures.push(
      // "exact count" (R6), not "exact snapshot": this is an exact
      // diagnostic *count* match, not a diagnostic-identity/fingerprint
      // match -- fixing one error while introducing a different one can
      // leave the count, and therefore this gate, unchanged.
      `Test type-check measured ${diagnostics.testDiagnostics.length} direct test diagnostic(s); expected exact count ${baseline.testDiagnosticCount}. ${direction}`,
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
    // split(...).includes(...) rather than startsWith("node_modules/"):
    // the latter is root-anchored, so a nested node_modules
    // (src/**/node_modules/**, e.g. a vendored/copied dependency) would
    // still be scanned. Matching any path segment closes that gap.
    !normalized.split("/").includes("node_modules") &&
    (normalized.startsWith("src/test/") ||
      normalized.includes("/__tests__/") ||
      /\.test\.(?:ts|tsx)$/.test(normalized))
  );
}
