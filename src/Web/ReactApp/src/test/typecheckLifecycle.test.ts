import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const packageDirectory = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "..",
);

describe("test type-check lifecycle", () => {
  it("runs the ratchet before coverage", async () => {
    const packageJson = JSON.parse(
      await readFile(path.join(packageDirectory, "package.json"), "utf8"),
    );

    expect(packageJson.scripts["test:coverage"]).toBeDefined();
    expect(packageJson.scripts["pretest:coverage"]).toBe(
      "npm run ci:typecheck-tests",
    );
  });
});

describe("application type-check lifecycle", () => {
  it("runs the application ratchet before build", async () => {
    const packageJson = JSON.parse(
      await readFile(path.join(packageDirectory, "package.json"), "utf8"),
    );

    expect(packageJson.scripts.build).toBeDefined();
    expect(packageJson.scripts.prebuild).toBe("npm run ci:typecheck-app");
    expect(packageJson.scripts["ci:typecheck-app"]).toBe(
      "node --test ./scripts/tests/test-typecheck-app.mjs && npm run typecheck:app",
    );
    expect(packageJson.scripts["typecheck:app"]).toBe(
      "node ./scripts/typecheck-app.mjs",
    );
  });
});

