import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  cp,
  mkdir,
  mkdtemp,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import {
  evaluate,
  isCheckableSourceFile,
  parseListFilesOutput,
  walkSrcFiles,
} from "../typecheck-src-coverage-core.mjs";
import { toRealPath } from "../typecheck-tests-core.mjs";

const scriptsDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const projectDirectory = path.resolve(scriptsDirectory, "..");

const okResult = { status: 0, signal: null, error: undefined };

function evaluateGate(overrides = {}) {
  return evaluate({
    appListFilesResult: okResult,
    appListFilesOutput: "src/services/example.ts",
    testListFilesResult: okResult,
    testListFilesOutput: "src/test/example.test.ts",
    walkedFiles: new Map([
      [
        toRealPath(path.resolve(projectDirectory, "src/services/example.ts")),
        "src/services/example.ts",
      ],
      [
        toRealPath(
          path.resolve(projectDirectory, "src/test/example.test.ts"),
        ),
        "src/test/example.test.ts",
      ],
    ]),
    directory: projectDirectory,
    ...overrides,
  });
}

test("isCheckableSourceFile accepts every TS-family extension including declaration files", () => {
  for (const accepted of [
    "src/foo.ts",
    "src/foo.tsx",
    "src/foo.mts",
    "src/foo.cts",
    "src/foo.d.ts",
    "src/foo.d.mts",
    "src/foo.d.cts",
  ]) {
    assert.equal(isCheckableSourceFile(accepted), true, accepted);
  }
});

test("isCheckableSourceFile rejects non-TypeScript extensions", () => {
  for (const rejected of [
    "src/foo.js",
    "src/foo.jsx",
    "src/foo.json",
    "src/foo.css",
    "src/foo.md",
    "src/foo",
  ]) {
    assert.equal(isCheckableSourceFile(rejected), false, rejected);
  }
});

test("accepts a file covered by exactly one project (app-only)", () => {
  assert.equal(evaluateGate().ok, true);
});

test("accepts a file covered by both projects without double-counting it as uncovered", () => {
  const result = evaluateGate({
    testListFilesOutput: "src/test/example.test.ts\nsrc/services/example.ts",
  });
  assert.equal(result.ok, true);
});

test("fails when a walked file matches neither project's --listFilesOnly output, naming it exactly", () => {
  const result = evaluateGate({
    walkedFiles: new Map([
      [
        toRealPath(path.resolve(projectDirectory, "src/services/example.ts")),
        "src/services/example.ts",
      ],
      [
        toRealPath(path.resolve(projectDirectory, "src/orphan.spec.ts")),
        "src/orphan.spec.ts",
      ],
    ]),
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /1 file\(s\)/);
  assert.match(result.message, /src\/orphan\.spec\.ts/);
  // Only the genuinely uncovered file is named -- the covered one must not
  // also appear in the failure list.
  assert.doesNotMatch(result.message, /src\/services\/example\.ts/);
});

test("reports every uncovered file, sorted, not just the first one found", () => {
  const result = evaluateGate({
    walkedFiles: new Map([
      [toRealPath(path.resolve(projectDirectory, "src/z-orphan.ts")), "src/z-orphan.ts"],
      [toRealPath(path.resolve(projectDirectory, "src/a-orphan.ts")), "src/a-orphan.ts"],
    ]),
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /2 file\(s\)/);
  const orderOfA = result.message.indexOf("src/a-orphan.ts");
  const orderOfZ = result.message.indexOf("src/z-orphan.ts");
  assert.ok(orderOfA >= 0 && orderOfZ >= 0 && orderOfA < orderOfZ);
});

test("fails closed when the application project's listing errors, independent of the test project", () => {
  const result = evaluateGate({
    appListFilesResult: { status: 1, signal: null, error: undefined },
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /application compiler could not list/);
});

test("fails closed when the test project's listing dies by signal", () => {
  const result = evaluateGate({
    testListFilesResult: { status: null, signal: "SIGKILL", error: undefined },
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /test compiler could not list/);
});

test("reports both project listing failures together, not sequentially, when both spawns fail", () => {
  const result = evaluateGate({
    appListFilesResult: { status: 1, signal: null, error: undefined },
    testListFilesResult: { status: null, signal: "SIGKILL", error: undefined },
  });
  assert.equal(result.ok, false);
  assert.match(result.message, /application compiler could not list/);
  assert.match(result.message, /test compiler could not list/);
});

test("parseListFilesOutput ignores paths outside src/, under node_modules (including a nested one), and non-checkable extensions", () => {
  const listing = [
    "src/services/example.ts",
    "node_modules/typescript/lib/lib.d.ts",
    "src/vendor/node_modules/pkg/index.d.ts",
    "../outside/example.ts",
    "src/services/example.js",
  ].join("\n");
  const files = parseListFilesOutput(listing, projectDirectory);
  assert.deepEqual([...files.values()], ["src/services/example.ts"]);
});

test("parseListFilesOutput dedups two listed spellings that resolve to the same real file via symlink", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-dedup-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src"), { recursive: true });
    const targetPath = path.join(fixtureDirectory, "src/real.ts");
    const linkPath = path.join(fixtureDirectory, "src/link.ts");
    await writeFile(targetPath, "export const a = 1;\n");

    try {
      await symlink(targetPath, linkPath, "file");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const files = parseListFilesOutput(
      "src/real.ts\nsrc/link.ts",
      fixtureDirectory,
    );
    assert.equal(files.size, 1);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("walkSrcFiles finds nested checkable files, skips node_modules under src/, and ignores non-checkable extensions", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-walk-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src/nested/deep"), {
      recursive: true,
    });
    await mkdir(path.join(fixtureDirectory, "src/node_modules/pkg"), {
      recursive: true,
    });
    await writeFile(
      path.join(fixtureDirectory, "src/top.ts"),
      "export const a = 1;\n",
    );
    await writeFile(
      path.join(fixtureDirectory, "src/nested/deep/leaf.spec.ts"),
      "export const a = 1;\n",
    );
    await writeFile(
      path.join(fixtureDirectory, "src/node_modules/pkg/index.ts"),
      "export const a = 1;\n",
    );
    await writeFile(
      path.join(fixtureDirectory, "src/styles.css"),
      "body {}\n",
    );

    const files = await walkSrcFiles(fixtureDirectory);
    assert.deepEqual(
      [...files.values()].sort(),
      ["src/nested/deep/leaf.spec.ts", "src/top.ts"],
    );
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("walkSrcFiles visits a symlink-only file and preserves the symlink path", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-walk-symlink-file-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "shared"), { recursive: true });
    const targetPath = path.join(fixtureDirectory, "shared/real.ts");
    const linkPath = path.join(fixtureDirectory, "src/link.ts");
    await writeFile(targetPath, "export const a = 1;\n");

    try {
      await symlink(targetPath, linkPath, "file");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const files = await walkSrcFiles(fixtureDirectory);
    assert.deepEqual([...files.values()], ["src/link.ts"]);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("walkSrcFiles follows a symlinked directory and reports children under the symlink path", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-walk-symlink-dir-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "shared/nested"), {
      recursive: true,
    });
    const targetPath = path.join(fixtureDirectory, "shared/nested/leaf.ts");
    const linkPath = path.join(fixtureDirectory, "src/linked-dir");
    await writeFile(targetPath, "export const a = 1;\n");

    try {
      await symlink(path.join(fixtureDirectory, "shared/nested"), linkPath, "dir");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const files = await walkSrcFiles(fixtureDirectory);
    assert.deepEqual([...files.values()], ["src/linked-dir/leaf.ts"]);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("walkSrcFiles avoids infinite recursion on a self-referential symlinked directory", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-walk-symlink-cycle-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src"), { recursive: true });
    await writeFile(
      path.join(fixtureDirectory, "src/real.ts"),
      "export const a = 1;\n",
    );

    try {
      await symlink(path.join(fixtureDirectory, "src"), path.join(fixtureDirectory, "src/loop"), "dir");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const files = await walkSrcFiles(fixtureDirectory);
    assert.deepEqual([...files.values()], ["src/real.ts"]);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("walkSrcFiles returns an empty result rather than throwing when src/ does not exist", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-nosrc-"),
  );

  try {
    const files = await walkSrcFiles(fixtureDirectory);
    assert.equal(files.size, 0);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("a symlink-only walked file is reported as uncovered until one project lists the symlink path", async (t) => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-symlink-gate-"),
  );

  try {
    await mkdir(path.join(fixtureDirectory, "src"), { recursive: true });
    await mkdir(path.join(fixtureDirectory, "shared"), { recursive: true });
    const targetPath = path.join(fixtureDirectory, "shared/real.ts");
    const linkPath = path.join(fixtureDirectory, "src/link.ts");
    await writeFile(targetPath, "export const a = 1;\n");

    try {
      await symlink(targetPath, linkPath, "file");
    } catch (error) {
      t.skip(`symlinks unavailable in this environment: ${error.message}`);
      return;
    }

    const walkedFiles = await walkSrcFiles(fixtureDirectory);
    const uncovered = evaluate({
      appListFilesResult: okResult,
      appListFilesOutput: "",
      testListFilesResult: okResult,
      testListFilesOutput: "",
      walkedFiles,
      directory: fixtureDirectory,
    });
    assert.equal(uncovered.ok, false);
    assert.match(uncovered.message, /src\/link\.ts/);

    const covered = evaluate({
      appListFilesResult: okResult,
      appListFilesOutput: "src/link.ts",
      testListFilesResult: okResult,
      testListFilesOutput: "",
      walkedFiles,
      directory: fixtureDirectory,
    });
    assert.equal(covered.ok, true);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

// CLI-level binding test, per #2826's own requested approach: a real fixture
// project with a file that matches neither tsconfig.app.json's nor
// tsconfig.test.json's file list, run through the actual production script
// end-to-end. The fixture lives entirely in a temp directory created and
// torn down at test runtime -- it is never written under this repo's own
// src/, so it cannot perturb the applicationDiagnosticCount /
// minimumAppFileCount / minimumTestFileCount baselines
// those two other gates are pinned to. This mirrors the existing
// hung-compiler CLI fixture precedent in test-typecheck-app.mjs (temp dir,
// fake tsc stub, cleanup in a finally block).
async function buildCoverageFixture(fixtureDirectory) {
  await mkdir(path.join(fixtureDirectory, "scripts"), { recursive: true });
  await mkdir(path.join(fixtureDirectory, "node_modules/typescript/bin"), {
    recursive: true,
  });
  await mkdir(path.join(fixtureDirectory, "src/test"), { recursive: true });
  await cp(
    path.join(scriptsDirectory, "typecheck-src-coverage.mjs"),
    path.join(fixtureDirectory, "scripts/typecheck-src-coverage.mjs"),
  );
  await cp(
    path.join(scriptsDirectory, "typecheck-src-coverage-core.mjs"),
    path.join(fixtureDirectory, "scripts/typecheck-src-coverage-core.mjs"),
  );
  await cp(
    path.join(scriptsDirectory, "typecheck-app-core.mjs"),
    path.join(fixtureDirectory, "scripts/typecheck-app-core.mjs"),
  );
  await cp(
    path.join(scriptsDirectory, "typecheck-tests-core.mjs"),
    path.join(fixtureDirectory, "scripts/typecheck-tests-core.mjs"),
  );
  // A stub tsc that answers --listFilesOnly based on which project (-p) was
  // requested, without ever touching real TypeScript project resolution --
  // this isolates the test to this gate's own diff logic, exactly as the
  // existing hung-compiler/signal-death/malformed-baseline CLI fixtures do
  // for the app and test ratchets.
  await writeFile(
    path.join(fixtureDirectory, "node_modules/typescript/bin/tsc"),
    [
      "const args = process.argv.slice(2);",
      'const project = args[args.indexOf("-p") + 1] || "";',
      'if (project.includes("tsconfig.app.json")) {',
      '  process.stdout.write("src/covered-by-app.ts\\n");',
      '} else if (project.includes("tsconfig.test.json")) {',
      '  process.stdout.write("src/test/covered-by-test.test.ts\\n");',
      "}",
    ].join("\n"),
  );
  await writeFile(
    path.join(fixtureDirectory, "src/covered-by-app.ts"),
    "export const a = 1;\n",
  );
  await writeFile(
    path.join(fixtureDirectory, "src/test/covered-by-test.test.ts"),
    "export const a = 1;\n",
  );
}

function runCoverageCli(fixtureDirectory) {
  return spawnSync(
    process.execPath,
    [path.join(fixtureDirectory, "scripts/typecheck-src-coverage.mjs")],
    { encoding: "utf8", timeout: 10_000 },
  );
}

test("CLI fails and names an orphan file matching neither tsconfig.app.json nor tsconfig.test.json (#2826 binding test)", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-cli-"),
  );

  try {
    await buildCoverageFixture(fixtureDirectory);
    // Matches neither the stub's app list nor its test list: this is the
    // exact gap #2826 describes (e.g. src/foo.spec.ts, src/foo.test.mts).
    await writeFile(
      path.join(fixtureDirectory, "src/orphan.spec.ts"),
      "export const a = 1;\n",
    );

    const result = runCoverageCli(fixtureDirectory);
    const output = `${result.stdout}${result.stderr}`;

    assert.notEqual(result.status, 0);
    assert.match(output, /1 file\(s\) under src\/ match neither/);
    assert.match(output, /src\/orphan\.spec\.ts/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});

test("CLI passes when every file under src/ is covered by one of the two projects", async () => {
  const fixtureDirectory = await mkdtemp(
    path.join(tmpdir(), "typecheck-src-coverage-cli-clean-"),
  );

  try {
    await buildCoverageFixture(fixtureDirectory);

    const result = runCoverageCli(fixtureDirectory);
    const output = `${result.stdout}${result.stderr}`;

    assert.equal(result.status, 0);
    assert.match(output, /Type-check project coverage passed/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});
