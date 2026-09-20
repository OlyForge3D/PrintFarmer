import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { cp, mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  clampedOverride,
  countApplicationFiles,
  countNoCheckFiles,
  evaluate,
} from "../typecheck-app-core.mjs";

const scriptsDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const projectDirectory = path.resolve(scriptsDirectory, "..");

const baseline = {
  applicationDiagnosticCount: 1,
  applicationNoCheckFileCount: 0,
  minimumAppFileCount: 1,
};
const fileDiagnostic =
  "src/services/example.ts(1,1): error TS2322: Type error.";

function evaluateGate(overrides = {}) {
  return evaluate({
    baseline,
    compilerResult: { status: 2, signal: null, error: undefined },
    listFilesResult: { status: 0, signal: null, error: undefined },
    output: fileDiagnostic,
    listFilesOutput: "src/services/example.ts",
    directory: projectDirectory,
    ...overrides,
  });
}

test("accepts an exact application diagnostic baseline", () => {
  assert.equal(evaluateGate().ok, true);
});

test("fails compiler signal death and null status before another guard can match", () => {
  const signal = evaluateGate({
    compilerResult: { status: null, signal: "SIGKILL", error: undefined },
  });
  const nullStatus = evaluateGate({
    compilerResult: { status: null, signal: null, error: undefined },
  });
  assert.match(signal.message, /did not complete/);
  assert.match(nullStatus.message, /did not complete/);
});

test("fails closed on a timed-out compiler with a distinct message (spawnSync timeout hardening)", () => {
  const timeoutError = Object.assign(new Error("spawnSync tsc ETIMEDOUT"), {
    code: "ETIMEDOUT",
  });
  const result = evaluateGate({
    compilerResult: { status: null, signal: null, error: timeoutError },
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /timed out/);
});

test("fails global compiler diagnostics before the nonzero-file fallback", () => {
  const result = evaluateGate({
    output: "error TS18003: No inputs were found in config file.",
  });
  assert.match(result.message, /global diagnostic/);
});

test("fails nonzero compiler exits without file diagnostics", () => {
  const result = evaluateGate({
    output: "",
    compilerResult: { status: 2, signal: null, error: undefined },
  });
  assert.match(result.message, /without file diagnostics/);
});

test("fails statuses 1 and 3 despite otherwise-valid diagnostics", () => {
  for (const status of [1, 3]) {
    const result = evaluateGate({
      compilerResult: { status, signal: null, error: undefined },
    });
    assert.match(result.message, new RegExp(`status ${status}`));
  }
});

test("fails above and below the exact diagnostic baseline", () => {
  const above = evaluateGate({
    output: `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
  });
  const below = evaluateGate({ output: "" });
  assert.match(
    above.message,
    /measured 2 diagnostic\(s\); expected exact count 1\. Fix the errors; do not raise the exact count/,
  );
  assert.match(
    below.message,
    /exited nonzero without file diagnostics/,
  );

  const staleBelow = evaluateGate({
    output: "",
    compilerResult: { status: 0, signal: null, error: undefined },
  });
  assert.match(
    staleBelow.message,
    /measured 0 diagnostic\(s\); expected exact count 1\. The exact count is stale; regenerate applicationDiagnosticCount in scripts\/app-typecheck-baseline\.json in the same commit/,
  );
});

test("fails an unsuccessful project-file listing before consuming its output (R2)", () => {
  for (const listFilesResult of [
    { status: 2, signal: null, error: undefined },
    { status: null, signal: "SIGKILL", error: undefined },
    { status: null, signal: null, error: new Error("list failed") },
  ]) {
    const result = evaluateGate({
      listFilesResult,
      listFilesOutput: "src/services/example.ts",
    });
    assert.match(result.message, /could not list its project files/);
  }
});

test("requests list-file output when the application file floor fails", () => {
  const result = evaluateGate({
    baseline: { ...baseline, minimumAppFileCount: 2 },
  });
  assert.equal(result.ok, false);
  assert.equal(result.showListFilesOutput, true);
});

test("does not request list-file output for an unrelated diagnostic failure", () => {
  const result = evaluateGate({
    output: `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
  });
  assert.equal(result.ok, false);
  assert.equal(result.showListFilesOutput, false);
});

test("fails when a counted application file is removed from the project", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-file-floor-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/services"), {
      recursive: true,
    });
    const firstFile = path.join(fixtureDirectory, "src/services/first.ts");
    const secondFile = path.join(fixtureDirectory, "src/services/second.ts");
    await writeFile(firstFile, "export const first = 1;\n");
    await writeFile(secondFile, "export const second = 2;\n");

    const listedFiles = "src/services/first.ts\nsrc/services/second.ts";
    const common = {
      baseline: { ...baseline, minimumAppFileCount: 2 },
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: fileDiagnostic,
      directory: fixtureDirectory,
    };

    assert.equal(countApplicationFiles(listedFiles, fixtureDirectory), 2);
    assert.equal(
      evaluate({ ...common, listFilesOutput: listedFiles }).ok,
      true,
    );

    await rm(secondFile);
    const remainingFile = "src/services/first.ts";
    assert.equal(countApplicationFiles(remainingFile, fixtureDirectory), 1);
    const result = evaluate({ ...common, listFilesOutput: remainingFile });
    assert.equal(result.ok, false);
    assert.match(
      result.message,
      /found 1 application file\(s\); expected at least 2\. Regenerate minimumAppFileCount/,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("fails above and below the exact @ts-nocheck file count (R2)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-nocheck-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/services"), {
      recursive: true,
    });
    await writeFile(
      path.join(fixtureDirectory, "src/services/example.ts"),
      "export const a = 1;\n",
    );
    await writeFile(
      path.join(fixtureDirectory, "src/services/nocheck.ts"),
      "// @ts-nocheck\nexport const b: number = 'not a number';\n",
    );

    // A NEW file carrying the directive contributes zero diagnostics either
    // way, so applicationDiagnosticCount alone cannot see it (the exploit
    // this closes). The nocheck count must catch it. output carries exactly
    // one diagnostic matching baseline.applicationDiagnosticCount so the
    // diagnostic-count gate stays satisfied and only the nocheck gate fails,
    // isolating the assertion below to the mechanism under test.
    const above = evaluate({
      baseline,
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: fileDiagnostic,
      listFilesOutput: "src/services/example.ts\nsrc/services/nocheck.ts",
      directory: fixtureDirectory,
    });
    assert.match(
      above.message,
      /found 1 @ts-nocheck file\(s\) under src\/; expected exact count 0\. A new or newly-@ts-nocheck'd application file was added/,
    );
    // The failure must name the offending file, not just a bare count
    // (Bishop, non-blocking review item).
    assert.match(above.message, /src\/services\/nocheck\.ts/);

    const below = evaluate({
      baseline: { ...baseline, applicationNoCheckFileCount: 1 },
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: fileDiagnostic,
      listFilesOutput: "src/services/example.ts",
      directory: fixtureDirectory,
    });
    assert.match(
      below.message,
      /found 0 @ts-nocheck file\(s\) under src\/; expected exact count 1\. The exact count is stale; regenerate applicationNoCheckFileCount in scripts\/app-typecheck-baseline\.json in the same commit/,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("countNoCheckFiles ignores files outside src/ and under node_modules/ (R2)", () => {
  const listing =
    "src/services/example.ts\nnode_modules/@types/x/index.d.ts\n../outside.ts";
  assert.deepEqual(countNoCheckFiles(listing, projectDirectory), {
    count: 0,
    paths: [],
  });
});

test("isAppSourceFile ignores a nested node_modules under src/, not only a root-level one (Bishop, non-blocking)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-nested-nm-"),
  );

  try {
    // startsWith("node_modules/") is root-anchored and would still scan
    // src/**/node_modules/**, e.g. a vendored/copied dependency directory.
    // The file below genuinely carries @ts-nocheck, so if the segment-based
    // exclusion (split("/").includes("node_modules")) were reverted to the
    // old root-anchored check, this nested file would wrongly be counted.
    await mkdir(
      path.join(fixtureDirectory, "src/vendor/node_modules/@types/x"),
      { recursive: true },
    );
    await writeFile(
      path.join(
        fixtureDirectory,
        "src/vendor/node_modules/@types/x/index.d.ts",
      ),
      "// @ts-nocheck\nexport const a: number = 1;\n",
    );

    const result = countNoCheckFiles(
      "src/vendor/node_modules/@types/x/index.d.ts",
      fixtureDirectory,
    );
    assert.deepEqual(result, { count: 0, paths: [] });
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("reports the diagnostic-count and @ts-nocheck-count failures together, not sequentially (R2, mirrors #2811 item 1)", () => {
  const result = evaluateGate({
    output: `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
    listFilesOutput: "src/services/example.ts\ndoes-not-exist.ts",
    baseline: { ...baseline, applicationNoCheckFileCount: 1 },
  });
  assert.match(result.message, /measured 2 diagnostic\(s\); expected exact count 1/);
  assert.match(
    result.message,
    /found 0 @ts-nocheck file\(s\) under src\/; expected exact count 1/,
  );
});

test("fails missing, malformed, and non-object baselines", () => {
  assert.match(
    evaluateGate({ baseline: { applicationNoCheckFileCount: 0 } }).message,
    /applicationDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: { applicationDiagnosticCount: 1 } }).message,
    /applicationNoCheckFileCount/,
  );
  assert.match(
    evaluateGate({
      baseline: {
        applicationDiagnosticCount: 1,
        applicationNoCheckFileCount: 0,
      },
    }).message,
    /minimumAppFileCount must be a positive integer\./,
  );
  for (const minimumAppFileCount of [0, -1]) {
    assert.equal(
      evaluateGate({
        baseline: {
          applicationDiagnosticCount: 1,
          applicationNoCheckFileCount: 0,
          minimumAppFileCount,
        },
      }).message,
      "Invalid application type-check baseline: minimumAppFileCount must be a positive integer.",
    );
  }
  assert.match(
    evaluateGate({
      baseline: { applicationDiagnosticCount: 1.5, applicationNoCheckFileCount: 0 },
    }).message,
    /applicationDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: null }).message,
    /baseline must be an object/,
  );
});

test("clampedOverride lets an in-range override through, and clamps everything else to the default (Hicks R6 blocking)", () => {
  // Valid, in-range overrides -- this is the only path CI/production is
  // expected to exercise (nothing sets these env vars outside tests).
  assert.equal(clampedOverride("300", 120_000), 300);
  assert.equal(clampedOverride("120000", 120_000), 120_000);
  assert.equal(clampedOverride("1", 120_000), 1);

  // Missing/unset entirely.
  assert.equal(clampedOverride(undefined, 120_000), 120_000);

  // Non-numeric / malformed strings.
  assert.equal(clampedOverride("abc", 120_000), 120_000);
  assert.equal(clampedOverride("", 120_000), 120_000);
  assert.equal(clampedOverride("  ", 120_000), 120_000);

  // Zero and negative -- must not disable or invert the bound.
  assert.equal(clampedOverride("0", 120_000), 120_000);
  assert.equal(clampedOverride("-1", 120_000), 120_000);

  // Fractional -- spawnSync's own timeout validation would reject this
  // outright, but the clamp itself must not accept it either.
  assert.equal(clampedOverride("1.5", 120_000), 120_000);

  // Non-finite.
  assert.equal(clampedOverride("Infinity", 120_000), 120_000);

  // Above the default ceiling -- an override may only shorten the bound,
  // never lengthen it.
  assert.equal(clampedOverride("999999999", 120_000), 120_000);

  // The exact case Bishop found accepted by a naive `Number(x) || default`:
  // 1e21 is a valid JS number (so `Number("1e21")` does not produce NaN),
  // but it exceeds Number.MAX_SAFE_INTEGER, so Number.isSafeInteger rejects
  // it and it falls back to the default instead of silently disabling the
  // timeout/maxBuffer bound.
  assert.equal(clampedOverride("1e21", 120_000), 120_000);
});

test("CLI fails without success or baseline-reduction advice after compiler signal death", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
      recursive: true,
    });
    await cp(
      path.join(scriptsDirectory, "typecheck-app.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-app-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app-core.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-tests-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-tests-core.mjs"),
    );
    await writeFile(
      path.join(fixtureDirectory, "scripts/app-typecheck-baseline.json"),
      JSON.stringify(baseline),
    );
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      'process.kill(process.pid, "SIGKILL");',
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      {
        encoding: "utf8",
        // Consistency with the hung-compiler and malformed-baseline tests
        // below: a real signal-death should return almost immediately, but
        // this bounds the test itself in case a future regression turns the
        // signal-death path into a hang.
        timeout: 10_000,
      },
    );
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.match(output, /TypeScript application compiler/);
    // R5: process.kill(pid, "SIGKILL") does not behave identically across
    // platforms -- on POSIX the process dies with signal:"SIGKILL",
    // status:null (the signal-death branch); on Windows, Node emulates it via
    // TerminateProcess and reports status:1, signal:null instead (the
    // status-fallback branch). Both messages happen to share the
    // "TypeScript application compiler" prefix, so asserting only that
    // substring passes regardless of which branch actually ran. Assert the
    // branch-distinguishing text so the test pins the platform-specific path
    // it is actually expected to take, rather than passing by coincidence.
    if (process.platform === "win32") {
      assert.match(output, /exited unexpectedly with status/);
    } else {
      assert.match(output, /did not complete successfully/);
    }
    assert.doesNotMatch(output, /typecheck-app\.mjs:\d+/);
    assert.doesNotMatch(output, /Application type-check passed/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("CLI kills a hung compiler via the spawnSync timeout instead of hanging forever", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-timeout-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
      recursive: true,
    });
    await cp(
      path.join(scriptsDirectory, "typecheck-app.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-app-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app-core.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-tests-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-tests-core.mjs"),
    );
    await writeFile(
      path.join(fixtureDirectory, "scripts/app-typecheck-baseline.json"),
      JSON.stringify(baseline),
    );
    const invocationLogPath = path.join(
      fixtureDirectory,
      "invocation-count.log",
    );
    // Never exits on its own -- without a spawnSync `timeout` option, this
    // would hang the CLI (and therefore the CI job / Docker build) forever.
    // TYPECHECK_APP_TIMEOUT_MS overrides the 120s production default so this
    // test does not itself take two minutes; production always uses the
    // default, unoverridden value. It also durably records each time it is
    // invoked (one line per spawn, appended synchronously before hanging)
    // so the assertions below can bind the compilerAlreadyFatal short-circuit
    // directly -- see the invocation-count assertion for why elapsed-time
    // alone cannot do this.
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      `require("node:fs").appendFileSync(${JSON.stringify(invocationLogPath)}, "invoked\\n");\nsetInterval(() => {}, 1000);`,
    );

    const start = Date.now();
    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      {
        encoding: "utf8",
        env: { ...process.env, TYPECHECK_APP_TIMEOUT_MS: "300" },
        // This outer bound is a test-harness watchdog, not the behavior
        // under test: if the production timeout in typecheck-app.mjs is
        // ever removed, the fixture's `tsc` never exits on its own and this
        // spawnSync call must not itself hang the test run (and, in CI, the
        // whole job) waiting on it. Ten seconds is far above the 300ms
        // production timeout this test expects to observe, so it never
        // fires on correct code -- only on a regression.
        timeout: 10_000,
      },
    );
    const elapsedMs = Date.now() - start;
    const output = `${result.stdout}${result.stderr}`;

    // Check the watchdog did not have to intervene before trusting any
    // assertion below: if it did, `result.status` is null and the CLI's own
    // timeout message never appears, which the assertions further down would
    // otherwise (mis)report as "does not match /timed out/" -- a confusing,
    // unattributed failure mode rather than a named one.
    assert.notEqual(
      result.error?.code,
      "ETIMEDOUT",
      "the outer test-harness watchdog fired, meaning the CLI's own " +
        "production timeout did not kill the hung compiler -- this is the " +
        "exact regression this test exists to catch",
    );
    assert.notEqual(result.status, 0);
    assert.match(output, /TypeScript application compiler timed out and was killed/);
    assert.doesNotMatch(output, /Application type-check passed/);
    // Binds the compilerAlreadyFatal short-circuit directly, rather than via
    // elapsed time: a hung/timed-out first compiler spawn is exactly the
    // fatal case typecheck-app.mjs's compilerAlreadyFatal check exists to
    // catch, so the second (--listFilesOnly) spawnSync must never run. If it
    // did, the same hung stub would be invoked and killed a second time,
    // appending a second "invoked" line. Reverting the short-circuit makes
    // this fail deterministically, independent of machine speed -- unlike an
    // elapsed-time bound, which a slow/loaded CI runner could still satisfy
    // even with the duplicate spawn (a fixed ~600ms real cost from two
    // sequential 300ms overrides is well within typical scheduling jitter).
    const invocationLines = (await readFile(invocationLogPath, "utf8"))
      .split("\n")
      .filter((line) => line.length > 0);
    assert.equal(
      invocationLines.length,
      1,
      `expected exactly 1 tsc invocation (the second, --listFilesOnly spawn ` +
        `must be short-circuited once the first spawn is already fatal), ` +
        `but observed ${invocationLines.length}`,
    );
    // Elapsed time remains a secondary, looser guard: it does not bind the
    // duplicate-spawn regression above (both an honest single spawn and a
    // reverted double spawn of a 300ms override comfortably clear 5s), but it
    // still catches an unrelated regression that let the production timeout
    // itself grow far past its override, e.g. to several seconds.
    assert.ok(
      elapsedMs < 5_000,
      `expected the CLI to return well under 5s once the compiler timeout fired; took ${elapsedMs}ms`,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("CLI fails closed with a nonzero exit on a malformed baseline JSON instead of silently falling through (blocking item 4)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-malformed-baseline-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
      recursive: true,
    });
    await cp(
      path.join(scriptsDirectory, "typecheck-app.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-app-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-app-core.mjs"),
    );
    await cp(
      path.join(scriptsDirectory, "typecheck-tests-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-tests-core.mjs"),
    );
    // Deliberately truncated/invalid JSON. `JSON.parse(readFileSync(...))`
    // in typecheck-app.mjs is a bare, unguarded call today -- it fails
    // closed via an uncaught throw, which is correct but easy to "clean up"
    // into a `try { ... } catch { return defaultBaseline }` that would fail
    // OPEN instead. This test never touches evaluate()'s already-parsed
    // object path; it goes through the real read+parse at the CLI entry
    // point, which is the only thing that can catch that regression.
    await writeFile(
      path.join(fixtureDirectory, "scripts/app-typecheck-baseline.json"),
      "{ applicationDiagnosticCount: 1, ",
    );
    // A tsc stub that exits cleanly with no output is intentional: with the
    // correct (unguarded) JSON.parse, it must never run at all, because the
    // parse throws first. The danger case this guards against is a future
    // `try { ... } catch { return defaultBaseline }` refactor -- if that
    // regression landed, the CLI would silently fall through to a default
    // baseline, spawn this well-behaved stub, see zero diagnostics matching
    // that default, and print a false "passed" with exit 0. A stub that
    // never terminates (as used in the timeout test above) would instead
    // make this test indistinguishable from a hang under its own 10s guard
    // timeout, which cannot tell "correctly failed closed" apart from "our
    // test's own safety timeout fired" -- so it must exit fast and clean.
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      "process.exit(0);",
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      { encoding: "utf8", timeout: 10_000 },
    );
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.doesNotMatch(output, /Application type-check passed/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("typecheck-app-core.mjs imports its @ts-nocheck detection from typecheck-tests-core.mjs and defines no local copy (R9, architectural pin for R2)", () => {
  // R2's entire anti-drift argument is "one scanner, shared by both gates."
  // A behavioral test cannot see the difference between importing the
  // shared helper and pasting an equivalent regex locally -- both would pass
  // every other test in this file. Only a source-text assertion pins the
  // single-scanner invariant itself.
  const source = readFileSync(
    path.join(scriptsDirectory, "typecheck-app-core.mjs"),
    "utf8",
  );

  assert.match(
    source,
    /import\s*\{[^}]*\bhasTsNoCheckDirective\b[^}]*\}\s*from\s*["']\.\/typecheck-tests-core\.mjs["']/,
  );
  assert.match(
    source,
    /import\s*\{[^}]*\btoRealPath\b[^}]*\}\s*from\s*["']\.\/typecheck-tests-core\.mjs["']/,
  );
  // No local re-declaration of the nocheck pattern -- a duplicate regex,
  // even one that behaves identically today, is exactly the drift risk R2
  // closed by sharing the detector. Match on regex-literal / function
  // declarations rather than the bare string "@ts-nocheck" (which
  // legitimately appears in this file's own comments and messages).
  assert.doesNotMatch(source, /const\s+\w*NO_?CHECK\w*\s*=\s*\//i);
  assert.doesNotMatch(source, /function\s+hasTsNoCheckDirective/);
});
