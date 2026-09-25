// Both compiler gates require zero diagnostics. Only coverage and @ts-nocheck
// counts remain baselined; no diagnostic allowance can hide error swaps (#2827).
import { isAbsolute, relative, resolve } from "node:path";
import { hasTsNoCheckDirective, toRealPath } from "./typecheck-tests-core.mjs";

export function classifyDiagnostics(output) {
  const lines = output.split(/\r?\n/).filter(Boolean);
  const fileDiagnostics = lines.filter((line) =>
    /^.+\(\d+,\d+\): error TS\d+:/.test(line),
  );
  const globalDiagnostics = lines.filter((line) => /^error TS\d+:/.test(line));

  return { fileDiagnostics, globalDiagnostics };
}

// Restricts a --listFilesOnly entry to real, first-party application source:
// under src/, not under node_modules (library .d.ts files tsc also lists),
// and not escaping the project directory. This mirrors the guard style of
// isTestFile in typecheck-tests-core.mjs, simplified because tsconfig.app.json
// itself already excludes every test/fixture root.
function isAppSourceFile(path, directory) {
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
    normalized.startsWith("src/")
  );
}

// A file carrying @ts-nocheck contributes no diagnostics, so a zero-error gate
// alone cannot detect that opt-out. Reuse the test gate's directive detector
// to keep both gates consistent.
//
// Returns both the count and the matched, project-relative paths (deduped by
// realpath, same as the count) so a gate failure can name the offending
// files instead of only reporting a number a reviewer has to hunt for.
export function countNoCheckFiles(listFilesOutput, directory) {
  const seen = new Map();

  for (const path of listFilesOutput.split(/\r?\n/)) {
    if (!path || !isAppSourceFile(path, directory)) {
      continue;
    }

    const absolute = resolve(directory, path);
    if (!hasTsNoCheckDirective(absolute)) {
      continue;
    }

    const real = toRealPath(absolute);
    if (!seen.has(real)) {
      seen.set(real, relative(directory, absolute).replaceAll("\\", "/"));
    }
  }

  return { count: seen.size, paths: [...seen.values()].sort() };
}

export function countApplicationFiles(listFilesOutput, directory) {
  const seen = new Set();

  for (const path of listFilesOutput.split(/\r?\n/)) {
    if (!path || !isAppSourceFile(path, directory)) {
      continue;
    }

    seen.add(toRealPath(resolve(directory, path)));
  }

  return seen.size;
}

// Bounds an env-var-supplied override for a spawnSync limit (timeout or
// maxBuffer) so the seam can only ever SHORTEN the given default, never
// lengthen, enlarge, or disable it. The env var is read unconditionally, in
// every run -- production, CI, and Docker alike -- so anything invalid,
// non-finite, or above the ceiling -- including a value technically
// representable as a JS number, such as 1e21 (which fails
// Number.isSafeInteger, since it exceeds Number.MAX_SAFE_INTEGER) -- must
// silently fall back to the default rather than disabling the bound it exists
// to enforce.
export function clampedOverride(envValue, defaultValue) {
  const parsed = Number(envValue);
  return Number.isSafeInteger(parsed) && parsed > 0 && parsed <= defaultValue
    ? parsed
    : defaultValue;
}

export function formatSinkOutput(stdout, stderr) {
  return `${stdout ?? ""}${stderr ?? ""}`;
}

export function validateBaseline(baseline) {
  if (!baseline || typeof baseline !== "object") {
    return "baseline must be an object.";
  }

  if (
    !Number.isInteger(baseline.applicationNoCheckFileCount) ||
    baseline.applicationNoCheckFileCount < 0
  ) {
    return "applicationNoCheckFileCount must be a non-negative integer.";
  }

  if (
    !Number.isInteger(baseline.minimumAppFileCount) ||
    baseline.minimumAppFileCount < 1
  ) {
    return "minimumAppFileCount must be a positive integer.";
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
      message: `Invalid application type-check baseline: ${baselineError}`,
      showListFilesOutput: false,
    };
  }

  // Node's spawnSync sets error.code === "ETIMEDOUT" specifically when the
  // `timeout` option kills a hung compiler; distinguishing that from an
  // ordinary crash/signal makes clear this is a bounded timeout, not an
  // unbounded hang that never resolves, and it must fail the gate exactly
  // like any other non-completion -- never fall through to a passing
  // evaluate.
  if (compilerResult.error?.code === "ETIMEDOUT") {
    return {
      ok: false,
      message: "TypeScript application compiler timed out and was killed.",
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
      message: "TypeScript application compiler did not complete successfully.",
      showListFilesOutput: false,
    };
  }

  const diagnostics = classifyDiagnostics(output);
  if (diagnostics.globalDiagnostics.length > 0) {
    return {
      ok: false,
      message: `TypeScript application compiler reported ${diagnostics.globalDiagnostics.length} global diagnostic(s).`,
      showListFilesOutput: false,
    };
  }

  // TypeScript 5.9's emitFilesAndReportErrorsAndGetExitStatus returns 2 for
  // --noEmit without outFile; status 1 means an emit was skipped, which this
  // invocation cannot legitimately produce.
  if (compilerResult.status !== 0 && compilerResult.status !== 2) {
    return {
      ok: false,
      message: `TypeScript application compiler exited unexpectedly with status ${compilerResult.status}.`,
      showListFilesOutput: false,
    };
  }

  if (compilerResult.status !== 0 && diagnostics.fileDiagnostics.length === 0) {
    return {
      ok: false,
      message:
        "TypeScript application compiler exited nonzero without file diagnostics.",
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
      message: "TypeScript application compiler could not list its project files.",
      showListFilesOutput: true,
    };
  }

  const applicationFileCount = countApplicationFiles(listFilesOutput, directory);
  const noCheck = countNoCheckFiles(listFilesOutput, directory);
  // Collect every gate failure before returning (#2811 item 1 lesson,
  // applied here too) so an edit that trips both the diagnostic count and the
  // @ts-nocheck count in the same run reports as one failure, not two
  // sequential, seemingly-unrelated ones.
  const failures = [];
  let showListFilesOutput = false;

  if (applicationFileCount < baseline.minimumAppFileCount) {
    failures.push(
      `TypeScript application compiler found ${applicationFileCount} application file(s); expected at least ${baseline.minimumAppFileCount}. Regenerate minimumAppFileCount in scripts/app-typecheck-baseline.json in the same commit.`,
    );
    showListFilesOutput = true;
  }

  if (diagnostics.fileDiagnostics.length > 0) {
    failures.push(
      `Application type-check measured ${diagnostics.fileDiagnostics.length} diagnostic(s); expected zero diagnostics. Fix the errors.`,
    );
  }

  if (noCheck.count !== baseline.applicationNoCheckFileCount) {
    const direction =
      noCheck.count > baseline.applicationNoCheckFileCount
        ? "A new or newly-@ts-nocheck'd application file was added; remove the directive instead of raising this count."
        : "The exact count is stale; regenerate applicationNoCheckFileCount in scripts/app-typecheck-baseline.json in the same commit.";
    // Name the offending files (sorted, project-relative) rather than
    // leaving a reviewer to hunt for them from a bare count.
    const pathList =
      noCheck.paths.length > 0
        ? `\n  ${noCheck.paths.join("\n  ")}`
        : "";
    failures.push(
      `Application type-check found ${noCheck.count} @ts-nocheck file(s) under src/; expected exact count ${baseline.applicationNoCheckFileCount}. ${direction}${pathList}`,
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
    message: `Application type-check passed with 0 diagnostic(s), ${applicationFileCount} application file(s), and ${noCheck.count}/${baseline.applicationNoCheckFileCount} @ts-nocheck file(s).`,
    showListFilesOutput: false,
  };
}
