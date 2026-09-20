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
      "npm run ci:typecheck-tests && npm run ci:typecheck-app",
    );
  });
});

describe("application type-check lifecycle", () => {
  // Deliberately NOT hooked to `prebuild`: `prebuild` fires before every
  // `vite build`, everywhere `npm run build` runs -- including Docker image
  // builds and deploy scripts, not just CI. Because the gate enforces exact
  // diagnostic-count equality in both directions, a developer who *fixes* an
  // unrelated type error would drop the app-build below baseline and be
  // unable to build at all until hand-editing a baseline JSON -- punishing
  // the exact behavior the ratchet exists to encourage. `pretest:coverage`
  // fires only as part of `npm run test:coverage`, which the `frontend` CI
  // job already runs (after `lint` and `build`), so the gate still runs and
  // still fails that job with no `.github/workflows/*` edit -- without
  // coupling it to every local/Docker production bundle.
  it("runs the application ratchet as part of the coverage lifecycle, not build", async () => {
    const packageJson = JSON.parse(
      await readFile(path.join(packageDirectory, "package.json"), "utf8"),
    );

    expect(packageJson.scripts.build).toBeDefined();
    expect(packageJson.scripts.prebuild).toBeUndefined();
    expect(packageJson.scripts["pretest:coverage"]).toBe(
      "npm run ci:typecheck-tests && npm run ci:typecheck-app",
    );
    expect(packageJson.scripts["ci:typecheck-app"]).toBe(
      "node --test ./scripts/tests/test-typecheck-app.mjs && npm run typecheck:app",
    );
    expect(packageJson.scripts["typecheck:app"]).toBe(
      "node ./scripts/typecheck-app.mjs",
    );
  });
});

