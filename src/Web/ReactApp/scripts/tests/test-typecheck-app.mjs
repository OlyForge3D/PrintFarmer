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
  applicationNoCheckFileCount: 0,
  minimumAppFileCount: 1,
};
const fileDiagnostic =
  "src/services/example.ts(1,1): error TS2322: Type error.";

function evaluateGate(overrides = {}) {
  return evaluate({
    baseline,
    compilerResult: { status: 0, signal: null, error: undefined },
    listFilesResult: { status: 0, signal: null, error: undefined },
    output: "",
    listFilesOutput: "src/services/example.ts",
    directory: projectDirectory,
    ...overrides,
  });
}

test("accepts zero diagnostics with the application-file and nocheck guards preserved", () => {
  const result = evaluateGate();
  assert.equal(result.ok, true);
  assert.match(
    result.message,
    /0 diagnostic\(s\), 1 application file\(s\), and 0\/0 @ts-nocheck file\(s\)/,
  );
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

test("fails statuses 1 and 3 before diagnostic checks", () => {
  for (const status of [1, 3]) {
    const result = evaluateGate({
      compilerResult: { status, signal: null, error: undefined },
      output: fileDiagnostic,
    });
    assert.match(result.message, new RegExp(`status ${status}`));
  }
});

test("fails every diagnostic even with a legacy allowance or compiler status zero (#2827)", () => {
  for (const output of [
    fileDiagnostic,
    fileDiagnostic.replace("example.ts", "replacement.ts"),
    fileDiagnostic.replace("TS2322", "TS2345"),
    "node_modules/example/index.d.ts(1,1): error TS2322: Type error.",
    `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
  ]) {
    for (const status of [0, 2]) {
      const result = evaluateGate({
        baseline: { ...baseline, applicationDiagnosticCount: 1 },
        compilerResult: { status, signal: null, error: undefined },
        output,
      });
      assert.equal(result.ok, false, `${status}: ${output}`);
      assert.match(result.message, /expected zero diagnostics\. Fix the errors/);
    }
  }
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
    // A failed listing is printed for diagnosis but never counted, preserving
    // R2's decisional invariant against misleading file-floor failures.
    assert.equal(result.showListFilesOutput, true);
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

// Pins each explicit `showListFilesOutput` return in evaluate() by strict
// equality (#2853). A typeof-only assertion would silently pass if a branch
// flipped its flag from false to true or vice versa -- but the CLI only
// prints listing output when the flag is true, so that flip would either
// suppress diagnostic listings on real failures or leak them on failures
// where the listing is meaningless. Naming each scenario and asserting the
// expected flag by strict equality makes such a regression fail loudly.
test("evaluate binds showListFilesOutput to the correct flag for every synchronous path (#2853)", () => {
  const timeoutError = Object.assign(new Error("spawnSync tsc ETIMEDOUT"), {
    code: "ETIMEDOUT",
  });

  const scenarios = [
    {
      name: "invalid baseline (null) short-circuits before compilerResult is read",
      overrides: { baseline: null },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "compiler timeout (ETIMEDOUT) is a bounded non-completion",
      overrides: {
        compilerResult: { status: null, signal: null, error: timeoutError },
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "compiler killed by a signal (SIGKILL) is a non-completion",
      overrides: {
        compilerResult: { status: null, signal: "SIGKILL", error: undefined },
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "compiler with a null status and no signal is a non-completion",
      overrides: {
        compilerResult: { status: null, signal: null, error: undefined },
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "global diagnostic (TS18003) is fatal without a listing request",
      overrides: {
        output: "error TS18003: No inputs were found in config file.",
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "compiler exits with an unexpected status (5) rather than 0 or 2",
      overrides: {
        compilerResult: { status: 5, signal: null, error: undefined },
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "compiler exits nonzero (2) with no file diagnostics",
      overrides: {
        // status 2 passes the unexpected-status guard (status === 0 || 2) and
        // reaches the "nonzero without file diagnostics" return; status 1 would
        // be intercepted earlier and leave that return unpinned (#2984 review).
        compilerResult: { status: 2, signal: null, error: undefined },
        output: "",
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "list-files spawn exits nonzero (cannot enumerate the project)",
      overrides: {
        listFilesResult: { status: 2, signal: null, error: undefined },
      },
      expectedOk: false,
      expectedShow: true,
    },
    {
      name: "diagnostic failure alone does not request listing output",
      overrides: {
        output: `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
      },
      expectedOk: false,
      expectedShow: false,
    },
    {
      name: "success return never requests listing output",
      overrides: {},
      expectedOk: true,
      expectedShow: false,
    },
  ];

  for (const scenario of scenarios) {
    const result = evaluateGate(scenario.overrides);
    assert.equal(result.ok, scenario.expectedOk, `${scenario.name}: ok`);
    assert.equal(
      result.showListFilesOutput,
      scenario.expectedShow,
      `${scenario.name}: showListFilesOutput`,
    );
  }
});

// The remaining paths (file-floor, @ts-nocheck, combined) route through
// countApplicationFiles / countNoCheckFiles, which read real files off disk,
// so they need on-disk fixtures rather than an override table (#2853).
test("evaluate: file-floor failure alone requests listing output (#2853)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-floor-only-"),
  );

  try {
    // Empty src/ => 0 application files; minimumAppFileCount = 1 trips the
    // floor. The compile pass is clean (0 diagnostics matches baseline) and
    // the listing spawn is clean, so ONLY the floor gate fails -- which is
    // exactly the branch that must set showListFilesOutput=true.
    await mkdir(path.join(fixtureDirectory, "src/services"), {
      recursive: true,
    });
    const result = evaluate({
      baseline: {
        applicationNoCheckFileCount: 0,
        minimumAppFileCount: 1,
      },
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: "",
      listFilesOutput: "",
      directory: fixtureDirectory,
    });

    assert.equal(result.ok, false);
    assert.equal(result.showListFilesOutput, true);
    assert.match(
      result.message,
      /found 0 application file\(s\); expected at least 1/,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("evaluate: @ts-nocheck-only failure does not request listing output (#2853)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-nocheck-only-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/services"), {
      recursive: true,
    });
    const nocheckPath = "src/services/nocheck.ts";
    await writeFile(
      path.join(fixtureDirectory, nocheckPath),
      "// @ts-nocheck\nexport const a = 1;\n",
    );

    // Compile pass and floor both pass; the ONLY gate that fails is the
    // @ts-nocheck count (1 observed vs 0 baseline). That failure lives in
    // the combined-failures block and must NOT set showListFilesOutput,
    // because the listing does not help diagnose it -- the offending path
    // list is already in the failure message itself.
    const result = evaluate({
      baseline: {
        applicationNoCheckFileCount: 0,
        minimumAppFileCount: 1,
      },
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: "",
      listFilesOutput: nocheckPath,
      directory: fixtureDirectory,
    });

    assert.equal(result.ok, false);
    assert.equal(result.showListFilesOutput, false);
    assert.match(
      result.message,
      /found 1 @ts-nocheck file\(s\) under src\/; expected exact count 0/,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("evaluate: combined floor + diagnostic failure reports both and requests listing (#2853)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-combined-"),
  );

  try {
    // Zero application files on disk (floor fails) AND the compile output
    // carries two file diagnostics under strict-zero (any diagnostic fails).
    // Accumulating both failures in a single evaluate call proves the
    // combined-failures block still reports them together -- and, because
    // the floor participates, still sets showListFilesOutput=true even
    // though a bare diagnostic failure on its own does not.
    await mkdir(path.join(fixtureDirectory, "src/services"), {
      recursive: true,
    });
    const result = evaluate({
      baseline: {
        applicationNoCheckFileCount: 0,
        minimumAppFileCount: 2,
      },
      compilerResult: { status: 2, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: `${fileDiagnostic}\n${fileDiagnostic.replace("(1,1)", "(2,1)")}`,
      listFilesOutput: "",
      directory: fixtureDirectory,
    });

    assert.equal(result.ok, false);
    assert.equal(result.showListFilesOutput, true);
    assert.match(
      result.message,
      /found 0 application file\(s\); expected at least 2/,
    );
    assert.match(
      result.message,
      /measured 2 diagnostic\(s\); expected zero diagnostics/,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
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
      output: "",
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

    // The directive hides all diagnostics, so only the nocheck guard fails.
    const above = evaluate({
      baseline,
      compilerResult: { status: 0, signal: null, error: undefined },
      listFilesResult: { status: 0, signal: null, error: undefined },
      output: "",
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
      output: "",
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
  assert.match(result.message, /measured 2 diagnostic\(s\); expected zero diagnostics/);
  assert.match(
    result.message,
    /found 0 @ts-nocheck file\(s\) under src\/; expected exact count 1/,
  );
});

test("fails missing, malformed, and non-object baselines", () => {
  for (const applicationNoCheckFileCount of [undefined, -1, 1.5, "0"]) {
    assert.equal(
      evaluateGate({
        baseline: { ...baseline, applicationNoCheckFileCount },
      }).message,
      "Invalid application type-check baseline: applicationNoCheckFileCount must be a non-negative integer.",
    );
  }
  for (const minimumAppFileCount of [undefined, 0, -1, 1.5, "1"]) {
    assert.equal(
      evaluateGate({
        baseline: { ...baseline, minimumAppFileCount },
      }).message,
      "Invalid application type-check baseline: minimumAppFileCount must be a positive integer.",
    );
  }
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

test("CLI prints nonempty list-file output when the application file floor fails", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-list-output-"),
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
      JSON.stringify({ ...baseline, minimumAppFileCount: 2 }),
    );
    const listedPath = "src/services/known.ts";
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      [
        `if (process.argv.includes("--listFilesOnly")) {`,
        `  process.stdout.write(${JSON.stringify(`${listedPath}\n`)});`,
        "}",
      ].join("\n"),
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      { encoding: "utf8" },
    );

    assert.notEqual(result.status, 0);
    assert.match(
      result.stderr,
      /TypeScript application compiler found 1 application file\(s\); expected at least 2/,
    );
    assert.match(result.stdout, /tsc --listFilesOnly output:\n/);
    assert.ok(
      result.stdout.includes(listedPath),
      `expected stdout to include ${listedPath}; got: ${result.stdout}`,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("CLI omits the list-file output label when the successful listing is empty (trim guard, #2853)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-app-empty-listing-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
      recursive: true,
    });
    for (const script of [
      "typecheck-app.mjs",
      "typecheck-app-core.mjs",
      "typecheck-tests-core.mjs",
    ]) {
      await cp(
        path.join(scriptsDirectory, script),
        path.join(fixtureDirectory, "scripts", script),
      );
    }
    // Zero-diagnostic baseline with a positive floor: the compile pass has
    // no diagnostics, so it succeeds; the listing pass exits 0 but writes
    // whitespace-only output; the floor then fails, which sets
    // evaluation.showListFilesOutput=true. The trim guard in
    // typecheck-app.mjs (`evaluation.showListFilesOutput && listFilesOutput.trim()`)
    // is therefore the only thing standing between the CLI and a bare
    // `tsc --listFilesOnly output:` label on stdout with nothing under it.
    // Deleting `&& listFilesOutput.trim()` must make the label-absence
    // assertion below fail.
    await writeFile(
      path.join(fixtureDirectory, "scripts/app-typecheck-baseline.json"),
      JSON.stringify({
        applicationNoCheckFileCount: 0,
        minimumAppFileCount: 1,
      }),
    );
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      [
        `if (process.argv.includes("--listFilesOnly")) {`,
        // Whitespace-only, so listFilesOutput is truthy but
        // listFilesOutput.trim() is empty -- the trim guard's precise job.
        `  process.stdout.write("   \\n\\t\\n");`,
        "} else {",
        "  // no diagnostics -- compile pass succeeds",
        "}",
      ].join("\n"),
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-app.mjs")],
      { encoding: "utf8", timeout: 30_000 },
    );
    // Guard against a harness watchdog masking a hang as the behavior
    // under test -- same pattern as the timeout fixture below.
    assert.notEqual(
      result.error?.code,
      "ETIMEDOUT",
      `test-harness watchdog fired: ${result.stderr}`,
    );

    assert.notEqual(result.status, 0);
    assert.match(
      result.stderr,
      /TypeScript application compiler found 0 application file\(s\); expected at least 1/,
    );
    // The trim guard MUST suppress the label when the successful listing
    // is whitespace-only. If the guard is removed, an empty
    // `tsc --listFilesOnly output:` header prints to stdout with nothing
    // under it -- meaningless noise that hides the real failure.
    assert.doesNotMatch(result.stdout, /--listFilesOnly output:/);
    assert.doesNotMatch(result.stderr, /Application type-check passed/);
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
      "{ minimumAppFileCount: 1, ",
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

// Obsolete count fields must never authorize diagnostics in either CLI.
for (const gate of [
  {
    name: "application",
    script: "typecheck-app.mjs",
    baselineFile: "app-typecheck-baseline.json",
    baseline: { ...baseline, applicationDiagnosticCount: 1 },
    listedFile: "src/services/example.ts",
  },
  {
    name: "test",
    script: "typecheck-tests.mjs",
    baselineFile: "test-typecheck-baseline.json",
    baseline: { minimumTestFileCount: 1, testDiagnosticCount: 1 },
    listedFile: "src/test/example.test.ts",
  },
]) {
  test(`${gate.name} CLI rejects same-count diagnostic swaps and accepts complete removal without a baseline edit (#2827)`, async () => {
    const fixtureDirectory = await mkdtemp(
      path.join(tmpdir(), `typecheck-${gate.name}-zero-diagnostics-`),
    );

    try {
      await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
      await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
        recursive: true,
      });
      for (const script of [
        "typecheck-app.mjs",
        "typecheck-app-core.mjs",
        "typecheck-tests.mjs",
        "typecheck-tests-core.mjs",
      ]) {
        await cp(
          path.join(scriptsDirectory, script),
          path.join(fixtureDirectory, "scripts", script),
        );
      }
      const baselinePath = path.join(
        fixtureDirectory, "scripts", gate.baselineFile,
      );
      const baselineJson = JSON.stringify(gate.baseline);
      await writeFile(baselinePath, baselineJson);
      const listedFile = path.join(fixtureDirectory, gate.listedFile);
      await mkdir(path.dirname(listedFile), { recursive: true });
      await writeFile(listedFile, "export const checked = 1;\n");
      const diagnosticPath = path.join(fixtureDirectory, "diagnostic.json");
      await writeFile(
        path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
        [
          'if (process.argv.includes("--listFilesOnly")) {',
          `  process.stdout.write(${JSON.stringify(`${gate.listedFile}\n`)});`,
          "} else {",
          `  const diagnostic = JSON.parse(require("node:fs").readFileSync(${JSON.stringify(diagnosticPath)}, "utf8"));`,
          "  process.stdout.write(diagnostic.output);",
          "  process.exitCode = diagnostic.status;",
          "}",
        ].join("\n"),
      );

      const original = `${gate.listedFile}(1,1): error TS2322: Type error.\n`;
      const outputs = [
        original,
        original.replace("example", "replacement"),
        original.replace("TS2322", "TS2345"),
        original.replace("Type error.", "Different error."),
      ];
      for (const output of [...outputs, ""]) {
        for (const status of output ? [0, 2] : [0]) {
          await writeFile(diagnosticPath, JSON.stringify({ output, status }));
          const result = spawnSync(
            process.execPath,
            [path.join(fixtureDirectory, "scripts", gate.script)],
            { encoding: "utf8", timeout: 10_000 },
          );
          assert.equal(result.error, undefined, result.error?.message);
          assert.equal(result.stdout, output);
          assert.equal(result.status, output ? 1 : 0, result.stderr);
          if (output) {
            assert.match(result.stderr, /expected zero diagnostics\. Fix the errors/);
            assert.doesNotMatch(result.stderr, /type-check passed/);
          } else {
            assert.match(result.stderr, /type-check passed with 0/);
            assert.doesNotMatch(result.stderr, /regenerate|stale/i);
          }
        }
      }
      assert.equal(await readFile(baselinePath, "utf8"), baselineJson);
    } finally {
      await rm(fixtureDirectory, { recursive: true, force: true });
    }
  });
}
