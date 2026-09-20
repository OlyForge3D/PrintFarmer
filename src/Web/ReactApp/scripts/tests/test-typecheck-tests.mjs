import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  cp,
  mkdtemp,
  mkdir,
  readFile,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { readFileSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  clampedOverride,
  classifyDiagnostics,
  countTestFiles,
  evaluate,
  hasTsNoCheckDirective,
  isCountableTestFile,
  isTestFile,
  toRealPath,
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

test("fails closed on a timed-out compiler with a distinct message (spawnSync timeout hardening)", () => {
  const timeoutError = Object.assign(new Error("spawnSync tsc ETIMEDOUT"), {
    code: "ETIMEDOUT",
  });
  const result = evaluateGate({
    compilerResult: { status: null, signal: "SIGTERM", error: timeoutError },
  });
  assert.equal(result.ok, false);
  assert.equal(result.showListFilesOutput, false);
  assert.match(result.message, /timed out/);
});

test("binds the listFiles ETIMEDOUT guard and preserves the list-file output flag", () => {
  const timeoutError = Object.assign(new Error("listFiles tsc ETIMEDOUT"), {
    code: "ETIMEDOUT",
  });
  const result = evaluateGate({
    listFilesResult: { status: null, signal: "SIGTERM", error: timeoutError },
    compilerResult: { status: 0, signal: null, error: undefined },
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /timed out/);
  assert.equal(result.showListFilesOutput, true);
});

test("clampedOverride only accepts shrink-only values and defaults invalid bounds", () => {
  const defaultValue = 120_000;
  const cases = [
    [undefined, defaultValue],
    ["", defaultValue],
    ["0", defaultValue],
    ["-1", defaultValue],
    ["NaN", defaultValue],
    ["abc", defaultValue],
    ["300", 300],
    ["120000", defaultValue],
    ["120001", defaultValue],
    ["1e21", defaultValue],
    ["1.5", defaultValue],
    ["119999", 119_999],
  ];

  for (const [value, expected] of cases) {
    assert.equal(
      clampedOverride(value, defaultValue),
      expected,
      `clampedOverride(${String(value)}, ${defaultValue}) should be ${expected}`,
    );
  }
});

test("typecheck-tests driver keeps both CLI sinks routed through the shared nullish-coalescing formatter", () => {
  const source = readFileSync(
    path.join(packageDirectory, "scripts/typecheck-tests.mjs"),
    "utf8",
  );
  const coreSource = readFileSync(
    path.join(packageDirectory, "scripts/typecheck-tests-core.mjs"),
    "utf8",
  );

  assert.match(
    source,
    /const output = formatSinkOutput\(compilerResult\.stdout, compilerResult\.stderr\);/,
  );
  assert.match(
    source,
    /const listFilesOutput = formatSinkOutput\(\s*listFilesResult\.stdout,\s*listFilesResult\.stderr,\s*\);/,
  );
  assert.match(coreSource, /return `\$\{stdout \?\? ""\}\$\{stderr \?\? ""\}`;/);
});

// Real CLI wiring: the first compiler run passes, then the listFiles pass hangs
// under --listFilesOnly. This binds the `showListFilesOutput` flag to the actual
// script path rather than a hand-built object.
test("CLI binds listFiles ETIMEDOUT to the real output sink and showListFilesOutput flag", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-tests-listfiles-timeout-"),
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
    const invocationLogPath = path.join(
      fixtureDirectory,
      "invocation-count.log",
    );
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      [
        'const fs = require("node:fs");',
        'const args = process.argv.slice(2);',
        'const log = process.env.INVOCATION_LOG_PATH;',
        'fs.appendFileSync(log, args.includes("--listFilesOnly") ? "listFiles\\n" : "compiler\\n");',
        'if (args.includes("--listFilesOnly")) {',
        '  process.stdout.write("src/test/example.test.ts\\n");',
        '  setInterval(() => {}, 1000);',
        '} else {',
        '  process.stdout.write("src/test/example.test.ts\\n");',
        '}',
      ].join("\n"),
    );

    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-tests.mjs")],
      {
        encoding: "utf8",
        env: {
          ...process.env,
          TYPECHECK_TEST_TIMEOUT_MS: "300",
          INVOCATION_LOG_PATH: invocationLogPath,
        },
        timeout: 10_000,
      },
    );
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.match(output, /timed out/);
    assert.match(output, /src\/test\/example\.test\.ts/);

    const invocationLines = (await readFile(invocationLogPath, "utf8"))
      .split("\n")
      .filter((line) => line.length > 0);
    assert.equal(invocationLines.length, 2);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("CLI times out the dedicated --listFilesOnly spawn before the outer fixture watchdog fires", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-tests-listfiles-watchdog-"),
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
    const invocationLogPath = path.join(
      fixtureDirectory,
      "invocation-order.log",
    );
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      [
        'const fs = require("node:fs");',
        'const args = process.argv.slice(2);',
        'const log = process.env.INVOCATION_LOG_PATH;',
        'const isListFilesOnly = args.includes("--listFilesOnly");',
        'fs.appendFileSync(log, isListFilesOnly ? "listFiles\\n" : "compiler\\n");',
        'if (isListFilesOnly) {',
        '  process.stdout.write("src/test/example.test.ts\\n");',
        '  setInterval(() => {}, 1000);',
        '} else {',
        '  process.stdout.write("src/test/example.test.ts\\n");',
        '  process.exit(0);',
        '}',
      ].join("\n"),
    );

    const start = Date.now();
    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-tests.mjs")],
      {
        encoding: "utf8",
        env: {
          ...process.env,
          TYPECHECK_TEST_TIMEOUT_MS: "300",
          INVOCATION_LOG_PATH: invocationLogPath,
        },
        timeout: 2_000,
      },
    );
    const elapsedMs = Date.now() - start;
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(
      result.error?.code,
      "ETIMEDOUT",
      "the outer fixture watchdog fired, meaning the dedicated listFilesOnly spawn lost its own production timeout",
    );
    assert.notEqual(result.status, 0);
    assert.match(output, /timed out/);
    assert.match(output, /src\/test\/example\.test\.ts/);
    assert.doesNotMatch(output, /Test type-check passed/);

    const invocationLines = (await readFile(invocationLogPath, "utf8"))
      .split("\n")
      .filter((line) => line.length > 0);
    assert.deepEqual(invocationLines, ["compiler", "listFiles"]);
    assert.ok(
      elapsedMs < 1_500,
      `expected the CLI to return well under 1.5s once the listFiles timeout fired; took ${elapsedMs}ms`,
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
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
    /measured 2 direct test diagnostic\(s\); expected exact count 1\. Fix the errors; do not raise the exact count/,
  );
  assert.match(
    below.message,
    /measured 0 direct test diagnostic\(s\); expected exact count 1\. The exact count is stale; regenerate testDiagnosticCount in scripts\/test-typecheck-baseline\.json in the same commit/,
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
    /measured 0 direct test diagnostic\(s\); expected exact count 1/,
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

test("classifyDiagnostics still gates a helper file's diagnostics via the broad isTestFile, not the strict isCountableTestFile (#2811 item 3 unbound classification)", () => {
  // countTestFiles/isCountableTestFile correctly stop a helper from
  // satisfying the file-count FLOOR (proven above), but nothing previously
  // proved classifyDiagnostics still uses the broad isTestFile to bucket
  // that same helper's DIAGNOSTICS. If classifyDiagnostics's testDiagnostics
  // filter were silently swapped from isTestFile to isCountableTestFile, a
  // literal bug in src/test/setup.ts (a non-`.test.ts`-named helper) would
  // stop being gated by testDiagnosticCount at all -- it would instead fall
  // into the untracked "imported application diagnostic" bucket, exactly
  // the silent evasion #2811 item 3/4 exists to close.
  const helperDiagnostic =
    "src/test/setup.ts(1,1): error TS2322: Type error.";
  const classification = classifyDiagnostics(helperDiagnostic, packageDirectory);

  assert.equal(classification.fileDiagnostics.length, 1);
  assert.equal(classification.testDiagnostics.length, 1);
  assert.equal(classification.testDiagnostics[0].path, "src/test/setup.ts");

  // End-to-end confirmation through evaluate(): the helper diagnostic alone
  // must satisfy testDiagnosticCount: 1 exactly as a real *.test.ts
  // diagnostic would -- it is not silently dropped into an unchecked bucket.
  const result = evaluateGate({ output: helperDiagnostic });
  assert.equal(result.ok, true);
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

test("countTestFiles excludes a test file carrying @ts-nocheck from the floor across evasion variants (#2811 item 5, R1)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-nocheck-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/test"), { recursive: true });

    // [name, file content, whether it must be EXCLUDED from the floor]
    const cases = [
      ["double-slash", "// @ts-nocheck\nexport const a: number = 'x';\n", true],
      // tsc honors triple-slash too (verified against the pinned compiler):
      // a pattern that only matched exactly "//" would miss this.
      ["triple-slash", "/// @ts-nocheck\nexport const a: number = 'x';\n", true],
      // Not independently verified against tsc, but pinned deliberately:
      // \/\/+ accepts any run of slashes rather than enumerating "// or ///"
      // specifically, so a currently-hypothetical four-slash form is also
      // excluded rather than silently falling through as a new hole.
      [
        "quad-slash",
        "//// @ts-nocheck\nexport const a: number = 'x';\n",
        true,
      ],
      [
        "bom-double-slash",
        "\uFEFF// @ts-nocheck\nexport const a: number = 'x';\n",
        true,
      ],
      // Negative case: tsc does NOT honor the block-comment form (verified:
      // still 1 diagnostic), so it must still count toward the floor.
      [
        "block-comment-negative",
        "/* @ts-nocheck */\nexport const a: number = 'x';\n",
        false,
      ],
    ];

    for (const [name, content] of cases) {
      await writeFile(
        path.join(fixtureDirectory, "src/test", `${name}.test.ts`),
        content,
      );
    }

    const listing = cases
      .map(([name]) => `src/test/${name}.test.ts`)
      .join("\n");
    const excludedCount = cases.filter(([, , mustExclude]) => mustExclude)
      .length;
    const expectedCount = cases.length - excludedCount;

    assert.equal(countTestFiles(listing, fixtureDirectory), expectedCount);

    // Also assert per-case so a regression names the exact evasion variant
    // that broke, rather than only an aggregate off-by-one.
    for (const [name, , mustExclude] of cases) {
      const absolute = path.join(
        fixtureDirectory,
        "src/test",
        `${name}.test.ts`,
      );
      assert.equal(
        hasTsNoCheckDirective(absolute),
        mustExclude,
        `expected hasTsNoCheckDirective(${name}) to be ${mustExclude}`,
      );
    }
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("hasTsNoCheckDirective fails closed on non-ENOENT I/O errors instead of silently returning false (#2811 hardening, R4)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-nocheck-io-"),
  );

  try {
    // Reading a directory as if it were a file fails with EISDIR, not
    // ENOENT -- a portable stand-in for any other unexpected I/O failure
    // (EACCES, EPERM, EBUSY, ...) that this repo cannot reliably trigger in
    // a shared, cross-platform test environment. Only ENOENT (a synthetic
    // path used purely in unit tests, e.g. above) may be swallowed; every
    // other failure must surface, not fail open and silently treat an
    // unreadable file as "not opted out".
    assert.throws(() => hasTsNoCheckDirective(fixtureDirectory), {
      code: "EISDIR",
    });
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("toRealPath collapses two casings of the same real file into one entry on Windows (#2811 hardening, R3)", async (t) => {
  if (process.platform !== "win32") {
    t.skip(
      "case-insensitive realpath normalization only applies on win32 filesystems",
    );
    return;
  }

  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-casefold-"),
  );

  try {
    const actualPath = path.join(fixtureDirectory, "CaseTest.test.ts");
    await writeFile(actualPath, "export const a = 1;\n");
    const differentCasePath = path.join(fixtureDirectory, "casetest.test.ts");

    // NTFS is case-insensitive by default, so both casings resolve to the
    // same on-disk file; toRealPath must fold them to one Set entry. This is
    // genuine real-filesystem evidence, but it only ever runs on a win32
    // host (CI here is ubuntu-latest/macos-latest only, never Windows), so
    // it must not be the only pin -- see the two injected-seam tests below,
    // which exercise the same contract deterministically on every platform.
    const seen = new Set([toRealPath(actualPath), toRealPath(differentCasePath)]);
    assert.equal(seen.size, 1);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

// R8: CI here (ubuntu-latest / macos-latest only, verified against
// ci.yml -- no Windows runner) never exercises win32 codepaths, so the
// win32-only test above never runs there and a revert of the
// `realpathSync.native ?? realpathSync` preference or the win32 casefold
// would still pass every check GitHub enforces. These two tests inject the
// platform string and the realpath implementation so the contract is pinned
// on any host, not only a developer's Windows machine.
test("toRealPath prefers realpathSync.native over the plain fallback when both are present (#2811 hardening, R3/R8)", () => {
  let nativeCalls = 0;
  let fallbackCalls = 0;
  function fakeRealpathImpl(inputPath) {
    fallbackCalls += 1;
    return inputPath;
  }
  fakeRealpathImpl.native = (inputPath) => {
    nativeCalls += 1;
    return inputPath;
  };

  toRealPath("/some/path", { platform: "linux", realpathImpl: fakeRealpathImpl });

  assert.equal(nativeCalls, 1);
  assert.equal(fallbackCalls, 0);
});

test("toRealPath folds casing on a simulated win32 platform regardless of the host OS (#2811 hardening, R3/R8)", () => {
  // Simulates the exact failure mode observed on a real Windows box: the
  // resolver (standing in for realpathSync.native) resolves the path but
  // does not itself normalize casing -- that must come from toRealPath's own
  // win32 casefold step, not be assumed to happen inside the realpath call.
  function fakeRealpathImpl(inputPath) {
    return inputPath;
  }

  const upper = toRealPath("C:\\Repo\\CaseTest.ts", {
    platform: "win32",
    realpathImpl: fakeRealpathImpl,
  });
  const lower = toRealPath("C:\\Repo\\casetest.ts", {
    platform: "win32",
    realpathImpl: fakeRealpathImpl,
  });

  assert.equal(upper, lower);

  // Same resolver, but on a non-win32 platform: casing must NOT be folded,
  // since POSIX filesystems are case-sensitive by design.
  const posixUpper = toRealPath("/repo/CaseTest.ts", {
    platform: "linux",
    realpathImpl: fakeRealpathImpl,
  });
  const posixLower = toRealPath("/repo/casetest.ts", {
    platform: "linux",
    realpathImpl: fakeRealpathImpl,
  });
  assert.notEqual(posixUpper, posixLower);
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
  assert.equal(
    // Nested node_modules (src/**/node_modules/**, e.g. a vendored/copied
    // dependency): startsWith("node_modules/") is root-anchored and would
    // miss this; split("/").includes("node_modules") catches it (Bishop,
    // non-blocking).
    isTestFile(
      "src/vendor/node_modules/@types/c/__tests__/d.d.ts",
      packageDirectory,
    ),
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

test("no *.spec.* file exists under src/ -- the gap neither gate covers is pinned to zero, not silently assumed closed (blocking item 5)", () => {
  // tsconfig.app.json excludes **/*.spec.* (and **/*.spec.e2e.*, a subset of
  // that pattern) from the application project; tsconfig.test.json's
  // `include` list never mentions *.spec.* either. A file matching that
  // name would therefore be compiled by neither `typecheck:app` nor
  // `typecheck:test` and could ship a type error unchecked. Closing that gap
  // by wiring *.spec.* into tsconfig.test.json would move
  // minimumTestFileCount/testDiagnosticCount for a scenario that has never
  // actually occurred (Bishop confirmed zero such files exist today), so
  // this pins the latent gap at its current, safe state instead: if anyone
  // ever adds a *.spec.* file under src/, this test fails immediately and
  // loudly, rather than the file silently compiling nowhere.
  const srcDirectory = path.join(packageDirectory, "src");
  const specFiles = readdirSync(srcDirectory, { recursive: true })
    .filter((entry) => /\.spec\./.test(path.basename(entry)))
    .map((entry) => entry.replaceAll("\\", "/"));

  assert.deepEqual(
    specFiles,
    [],
    `found *.spec.* file(s) under src/ that neither typecheck:app nor ` +
      `typecheck:test compiles: ${specFiles.join(", ")}. Add coverage for ` +
      `these files (e.g. wire *.spec.* into tsconfig.test.json and ` +
      `isTestFile, re-baselining testDiagnosticCount/minimumTestFileCount ` +
      `deliberately) before removing this guard.`,
  );
});

test("CLI kills a hung compiler via the spawnSync timeout instead of hanging forever", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-tests-timeout-"),
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
    const invocationLogPath = path.join(
      fixtureDirectory,
      "invocation-count.log",
    );
    // Never exits on its own -- without a spawnSync `timeout` option, this
    // would hang the CLI (and therefore the CI job) forever. The override is
    // intentionally tiny so the test finishes quickly while still exercising
    // production's timeout behavior; the outer harness watchdog remains a
    // safety net only for regressions that remove the timeout entirely.
    await writeFile(
      path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
      `require("node:fs").appendFileSync(${JSON.stringify(invocationLogPath)}, "invoked\\n");\nsetInterval(() => {}, 1000);`,
    );

    const start = Date.now();
    const result = spawnSync(
      process.execPath,
      [path.join(fixtureDirectory, "scripts/typecheck-tests.mjs")],
      {
        encoding: "utf8",
        env: { ...process.env, TYPECHECK_TEST_TIMEOUT_MS: "300" },
        timeout: 10_000,
      },
    );
    const elapsedMs = Date.now() - start;
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(
      result.error?.code,
      "ETIMEDOUT",
      "the outer test-harness watchdog fired, meaning the CLI's own " +
        "production timeout did not kill the hung compiler -- this is the " +
        "exact regression this test exists to catch",
    );
    assert.notEqual(result.status, 0);
    assert.match(output, /TypeScript test compiler timed out and was killed/);
    assert.doesNotMatch(output, /Test type-check passed/);

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
    assert.ok(
      elapsedMs < 5_000,
      `expected the CLI to return well under 5s once the compiler timeout fired; took ${elapsedMs}ms`,
    );

    assert.doesNotMatch(output, /typecheck-tests\.mjs:\d+/);
    assert.doesNotMatch(output, /baseline is stale/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
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
    // R7 (mirrors R5): process.kill(pid, "SIGKILL") does not behave
    // identically across platforms -- on POSIX the process dies with
    // signal:"SIGKILL", status:null (the signal-death branch); on Windows,
    // Node emulates it via TerminateProcess and reports status:1,
    // signal:null instead (the status-fallback branch). Both messages
    // happen to share the "TypeScript test compiler" prefix, so asserting
    // only that substring passes regardless of which branch actually ran.
    // Assert the branch-distinguishing text so the test pins the
    // platform-specific path it is actually expected to take, rather than
    // passing by coincidence.
    if (process.platform === "win32") {
      assert.match(output, /exited unexpectedly with status/);
    } else {
      assert.match(output, /did not complete successfully/);
    }
    assert.doesNotMatch(output, /typecheck-tests\.mjs:\d+/);
    assert.doesNotMatch(output, /Test type-check passed/);
    assert.doesNotMatch(output, /baseline is stale/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});
