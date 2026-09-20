// Ratchet for application source (src/, excluding test/fixture roots) type
// errors, mirroring scripts/typecheck-tests-core.mjs. tsconfig.app.json
// already excludes src/test/**, __tests__/**, and *.test.*/*.spec.* files,
// so every diagnostic this module sees is genuine application source — there
// is no test/application split to classify here, unlike the test ratchet.

export function classifyDiagnostics(output) {
  const lines = output.split(/\r?\n/).filter(Boolean);
  const fileDiagnostics = lines.filter((line) =>
    /^.+\(\d+,\d+\): error TS\d+:/.test(line),
  );
  const globalDiagnostics = lines.filter((line) => /^error TS\d+:/.test(line));

  return { fileDiagnostics, globalDiagnostics };
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

  return undefined;
}

export function evaluate({ baseline, compilerResult, output }) {
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

  if (diagnostics.fileDiagnostics.length !== baseline.applicationDiagnosticCount) {
    const direction =
      diagnostics.fileDiagnostics.length > baseline.applicationDiagnosticCount
        ? "Fix the errors; do not raise the exact snapshot."
        : "The exact snapshot is stale; regenerate applicationDiagnosticCount in scripts/app-typecheck-baseline.json in the same commit.";
    return {
      ok: false,
      message: `Application type-check measured ${diagnostics.fileDiagnostics.length} diagnostic(s); expected exact snapshot ${baseline.applicationDiagnosticCount}. ${direction}`,
    };
  }

  return {
    ok: true,
    message: `Application type-check passed with ${diagnostics.fileDiagnostics.length}/${baseline.applicationDiagnosticCount} baseline diagnostic(s). See #2820 to drive this to zero.`,
  };
}
