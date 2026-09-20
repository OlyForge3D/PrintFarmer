// Ratchet for application source (src/, excluding test/fixture roots) type
// errors, mirroring scripts/typecheck-tests-core.mjs. tsconfig.app.json
// already excludes src/test/**, __tests__/**, and *.test.*/*.spec.* files,
// so every diagnostic this module sees is genuine application source — there
// is no test/application split to classify here, unlike the test ratchet.
//
// Known limitation (R6): applicationDiagnosticCount is an exact *count* of
// diagnostics, not an exact *set*/fingerprint of them. Fixing one error while
// introducing a different one can leave the count -- and therefore this gate
// -- unchanged. Closing that gap would require per-diagnostic identity
// (e.g. rule + location) tracking, which is a separate, larger change; see
// the test ratchet in scripts/typecheck-tests-core.mjs, which has the same
// count-based limitation by the same design.
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

// Counts application source files carrying a `@ts-nocheck` directive (#2811
// item 5 / R2). Reuses the exact same detector the test ratchet uses so the
// two gates cannot silently drift apart. The exploit this closes: adding the
// directive to an EXISTING file removes its diagnostics, which already trips
// the `!==` diagnostic-count comparison below and fails loudly. But adding a
// brand-NEW file that carries the directive from creation contributes zero
// diagnostics either way -- applicationDiagnosticCount stays exactly at
// baseline and the gate would otherwise pass silently, with no baseline edit
// and no signal to reviewers. Gating this count separately closes that
// silent path.
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

export function validateBaseline(baseline) {
  if (!baseline || typeof baseline !== "object") {
    return "baseline must be an object.";
  }

  if (
    !Number.isInteger(baseline.applicationDiagnosticCount) ||
    baseline.applicationDiagnosticCount < 0
  ) {
    return "applicationDiagnosticCount must be a non-negative integer.";
  }

  if (
    !Number.isInteger(baseline.applicationNoCheckFileCount) ||
    baseline.applicationNoCheckFileCount < 0
  ) {
    return "applicationNoCheckFileCount must be a non-negative integer.";
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
    };
  }

  const diagnostics = classifyDiagnostics(output);
  if (diagnostics.globalDiagnostics.length > 0) {
    return {
      ok: false,
      message: `TypeScript application compiler reported ${diagnostics.globalDiagnostics.length} global diagnostic(s).`,
    };
  }

  // TypeScript 5.9's emitFilesAndReportErrorsAndGetExitStatus returns 2 for
  // --noEmit without outFile; status 1 means an emit was skipped, which this
  // invocation cannot legitimately produce.
  if (compilerResult.status !== 0 && compilerResult.status !== 2) {
    return {
      ok: false,
      message: `TypeScript application compiler exited unexpectedly with status ${compilerResult.status}.`,
    };
  }

  if (compilerResult.status !== 0 && diagnostics.fileDiagnostics.length === 0) {
    return {
      ok: false,
      message:
        "TypeScript application compiler exited nonzero without file diagnostics.",
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
    };
  }

  const noCheck = countNoCheckFiles(listFilesOutput, directory);
  // Collect every gate failure before returning (#2811 item 1 lesson,
  // applied here too) so an edit that trips both the diagnostic count and the
  // @ts-nocheck count in the same run reports as one failure, not two
  // sequential, seemingly-unrelated ones.
  const failures = [];

  if (diagnostics.fileDiagnostics.length !== baseline.applicationDiagnosticCount) {
    const direction =
      diagnostics.fileDiagnostics.length > baseline.applicationDiagnosticCount
        ? "Fix the errors; do not raise the exact count."
        : "The exact count is stale; regenerate applicationDiagnosticCount in scripts/app-typecheck-baseline.json in the same commit.";
    failures.push(
      `Application type-check measured ${diagnostics.fileDiagnostics.length} diagnostic(s); expected exact count ${baseline.applicationDiagnosticCount}. ${direction}`,
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
    };
  }

  return {
    ok: true,
    message: `Application type-check passed with ${diagnostics.fileDiagnostics.length}/${baseline.applicationDiagnosticCount} baseline diagnostic(s) (exact count, not diagnostic identity) and ${noCheck.count}/${baseline.applicationNoCheckFileCount} @ts-nocheck file(s). See #2820 to drive the diagnostic count to zero.`,
  };
}
