import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
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
  assert.equal(countNoCheckFiles(listing, projectDirectory), 0);
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
