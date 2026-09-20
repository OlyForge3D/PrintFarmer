import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cp, mkdtemp, mkdir, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { evaluate, isTestFile } from "../typecheck-tests-core.mjs";

const packageDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
);
const baseline = { testDiagnosticCount: 1, minimumTestFileCount: 1 };
const testDiagnostic =
  "src/test/example.test.ts(1,1): error TS2322: Type error.";
const appDiagnostic = "src/services/example.ts(1,1): error TS2322: Type error.";
const listFiles = "src/test/example.test.ts";

function evaluateGate(overrides = {}) {
  return evaluate({
    baseline,
    compilerResult: { status: 2, signal: null, error: undefined },
    listFilesResult: { status: 0, signal: null, error: undefined },
    output: testDiagnostic,
    listFilesOutput: listFiles,
    directory: packageDirectory,
    ...overrides,
  });
}

test("accepts an exact test diagnostic baseline", () => {
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

test("fails an unsuccessful project-file listing", () => {
  const result = evaluateGate({
    listFilesResult: { status: 2, signal: null, error: undefined },
  });
  assert.match(result.message, /could not list/);
});

test("fails below the exact test-file floor", () => {
  const result = evaluateGate({
    baseline: { ...baseline, minimumTestFileCount: 2 },
  });
  assert.match(result.message, /below the 2-file floor/);
});

test("fails above and below the exact diagnostic baseline", () => {
  const above = evaluateGate({
    output: `${testDiagnostic}\n${testDiagnostic.replace("(1,1)", "(2,1)")}`,
  });
  const below = evaluateGate({ output: appDiagnostic });
  assert.match(above.message, /do not raise the baseline/);
  assert.match(below.message, /baseline is stale/);
});

test("fails missing, malformed, and non-object baselines", () => {
  assert.match(
    evaluateGate({ baseline: { minimumTestFileCount: 1 } }).message,
    /testDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: { testDiagnosticCount: 1 } }).message,
    /minimumTestFileCount/,
  );
  assert.match(
    evaluateGate({
      baseline: { testDiagnosticCount: 1.5, minimumTestFileCount: 1 },
    }).message,
    /testDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: null }).message,
    /baseline must be an object/,
  );
});

test("classifies project-relative and absolute test paths while excluding dependencies and outside files", () => {
  assert.equal(isTestFile("src/test/a.test.ts", packageDirectory), true);
  assert.equal(
    isTestFile(
      path.join(packageDirectory, "src/x/__tests__/b.tsx"),
      packageDirectory,
    ),
    true,
  );
  assert.equal(
    isTestFile("node_modules/@types/c/__tests__/d.d.ts", packageDirectory),
    false,
  );
  assert.equal(isTestFile("../../outside.test.ts", packageDirectory), false);
  assert.equal(isTestFile("src/services/e.ts", packageDirectory), false);
});

test("CLI fails without success or baseline-reduction advice after compiler signal death", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-tests-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
      recursive: true,
    });
    await cp(
      path.join(packageDirectory, "scripts/typecheck-tests.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-tests.mjs"),
    );
    await cp(
      path.join(packageDirectory, "scripts/typecheck-tests-core.mjs"),
      path.join(fixtureDirectory, "scripts/typecheck-tests-core.mjs"),
    );
    await writeFile(
      path.join(fixtureDirectory, "scripts/test-typecheck-baseline.json"),
      JSON.stringify(baseline),
    );
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      'process.kill(process.pid, "SIGKILL");',
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-tests.mjs")],
      {
        encoding: "utf8",
      },
    );
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.match(output, /TypeScript test compiler/);
    assert.doesNotMatch(output, /typecheck-tests\.mjs:\d+/);
    assert.doesNotMatch(output, /Test type-check passed/);
    assert.doesNotMatch(output, /baseline is stale/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});
