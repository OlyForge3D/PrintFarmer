import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { cp, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { evaluate } from "../typecheck-app-core.mjs";

const scriptsDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);

const baseline = { applicationDiagnosticCount: 1 };
const fileDiagnostic =
  "src/services/example.ts(1,1): error TS2322: Type error.";

function evaluateGate(overrides = {}) {
  return evaluate({
    baseline,
    compilerResult: { status: 2, signal: null, error: undefined },
    output: fileDiagnostic,
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
    /measured 2 diagnostic\(s\); expected exact snapshot 1\. Fix the errors; do not raise the exact snapshot/,
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
    /measured 0 diagnostic\(s\); expected exact snapshot 1\. The exact snapshot is stale; regenerate applicationDiagnosticCount in scripts\/app-typecheck-baseline\.json in the same commit/,
  );
});

test("fails missing, malformed, and non-object baselines", () => {
  assert.match(
    evaluateGate({ baseline: {} }).message,
    /applicationDiagnosticCount/,
  );
  assert.match(
    evaluateGate({ baseline: { applicationDiagnosticCount: 1.5 } }).message,
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
    assert.doesNotMatch(output, /typecheck-app\.mjs:\d+/);
    assert.doesNotMatch(output, /Application type-check passed/);
  } finally {
    await rm(fixtureDirectory, { recursive: true, force: true });
  }
});
