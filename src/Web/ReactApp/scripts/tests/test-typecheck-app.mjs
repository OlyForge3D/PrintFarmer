import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { cp, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { countNoCheckFiles, evaluate } from "../typecheck-app-core.mjs";

const scriptsDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const projectDirectory = path.resolve(scriptsDirectory, "..");

const baseline = { applicationDiagnosticCount: 1, applicationNoCheckFileCount: 0 };
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

test("fails an unsuccessful project-file listing (R2)", () => {
  const result = evaluateGate({
    listFilesResult: { status: 2, signal: null, error: undefined },
  });
  assert.match(result.message, /could not list its project files/);
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
      baseline: { applicationDiagnosticCount: 1.5, applicationNoCheckFileCount: 0 },
    }).message,
    /applicationDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: null }).message,
    /baseline must be an object/,
  );
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
    // Never exits on its own -- without a spawnSync `timeout` option, this
    // would hang the CLI (and therefore the CI job / Docker build) forever.
    // TYPECHECK_APP_TIMEOUT_MS overrides the 120s production default so this
    // test does not itself take two minutes; production always uses the
    // default, unoverridden value.
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      "setInterval(() => {}, 1000);",
    );

    const start = Date.now();
    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      {
        encoding: "utf8",
        env: { ...process.env, TYPECHECK_APP_TIMEOUT_MS: "300" },
      },
    );
    const elapsedMs = Date.now() - start;
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.match(output, /TypeScript application compiler timed out and was killed/);
    assert.doesNotMatch(output, /Application type-check passed/);
    // The hung tsc must actually be killed near the overridden 300ms bound,
    // not merely reported as timed out while the process (and the wrapping
    // CLI's wait on it) continues indefinitely in the background.
    assert.ok(
      elapsedMs < 30_000,
      `expected the CLI to return well under 30s once the compiler timeout fired; took ${elapsedMs}ms`,
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
