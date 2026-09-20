import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  cp,
  mkdtemp,
  mkdir,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  countTestFiles,
  evaluate,
  isCountableTestFile,
  isTestFile,
} from "../typecheck-tests-core.mjs";

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
  assert.match(
    result.message,
    /found 1 test file\(s\); expected at least 2\. Regenerate minimumTestFileCount in scripts\/test-typecheck-baseline\.json in the same commit/,
  );
});

test("fails above and below the exact diagnostic baseline", () => {
  const above = evaluateGate({
    output: `${testDiagnostic}\n${testDiagnostic.replace("(1,1)", "(2,1)")}`,
  });
  const below = evaluateGate({ output: appDiagnostic });
  assert.match(
    above.message,
    /measured 2 direct test diagnostic\(s\); expected exact snapshot 1\. Fix the errors; do not raise the exact snapshot/,
  );
  assert.match(
    below.message,
    /measured 0 direct test diagnostic\(s\); expected exact snapshot 1\. The exact snapshot is stale; regenerate testDiagnosticCount in scripts\/test-typecheck-baseline\.json in the same commit/,
  );
});

test("reports the file-count and diagnostic-snapshot failures together, not sequentially (#2811 item 1)", () => {
  const result = evaluateGate({
    baseline: { testDiagnosticCount: 1, minimumTestFileCount: 2 },
    output: appDiagnostic,
  });
  assert.match(result.message, /found 1 test file\(s\); expected at least 2/);
  assert.match(
    result.message,
    /measured 0 direct test diagnostic\(s\); expected exact snapshot 1/,
  );
  assert.equal(result.showListFilesOutput, true);
});

test("isCountableTestFile excludes non-test helpers under test roots that isTestFile still accepts (#2811 item 3)", () => {
  const helper = "src/test/setup.ts";
  const fixtureUnderTests = "src/x/__tests__/fixture.ts";
  assert.equal(isTestFile(helper, packageDirectory), true);
  assert.equal(isCountableTestFile(helper, packageDirectory), false);
  assert.equal(isTestFile(fixtureUnderTests, packageDirectory), true);
  assert.equal(isCountableTestFile(fixtureUnderTests, packageDirectory), false);
  assert.equal(isCountableTestFile("src/test/a.test.ts", packageDirectory), true);
});

test("countTestFiles ignores non-test helper files even though isTestFile still classifies their diagnostics (#2811 items 3-4)", () => {
  const listing = "src/test/a.test.ts\nsrc/test/helper.ts";
  assert.equal(countTestFiles(listing, packageDirectory), 1);
  // The helper's diagnostics remain gated by testDiagnostics/isTestFile even
  // though it no longer counts toward the file-count floor.
  assert.equal(isTestFile("src/test/helper.ts", packageDirectory), true);
});

test("countTestFiles dedups a symlink to an already-counted test file via realpath (#2811 item 2)", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-dedup-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/test"), { recursive: true });
    const targetPath = path.join(fixtureDirectory, "src/test/real.test.ts");
    const linkPath = path.join(fixtureDirectory, "src/test/link.test.ts");
    await writeFile(targetPath, "export const a = 1;\n");

    try {
      await symlink(targetPath, linkPath, "file");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const listing = "src/test/real.test.ts\nsrc/test/link.test.ts";
    assert.equal(countTestFiles(listing, fixtureDirectory), 1);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("countTestFiles excludes a test file carrying // @ts-nocheck from the floor (#2811 item 5)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-nocheck-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/test"), { recursive: true });
    await writeFile(
      path.join(fixtureDirectory, "src/test/normal.test.ts"),
      "export const a = 1;\n",
    );
    await writeFile(
      path.join(fixtureDirectory, "src/test/nocheck.test.ts"),
      "// @ts-nocheck\nexport const b: number = 'not a number';\n",
    );

    const listing = "src/test/normal.test.ts\nsrc/test/nocheck.test.ts";
    assert.equal(countTestFiles(listing, fixtureDirectory), 1);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
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

test(
  "excludes paths on a different Windows drive",
  { skip: process.platform !== "win32" },
  () => {
    // Different drive roots only exist on Windows; on POSIX relative() cannot
    // return an absolute path, so this covers the Windows-only isAbsolute guard.
    assert.equal(isTestFile("Z:/outside.test.ts", packageDirectory), false);
  },
);

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
