import { expect, test } from '@playwright/test';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { basename, isAbsolute, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertAccessibility, assertNoScenarioNetworkViolations, assertPinnedFonts, assertRenderingEnvironment, prepareVisualScene } from './visual-test-helpers';
import { captureOrigin } from './fixtures';
import { visualScenes } from './visual-scenes';
import { assertApprovedGolden, assertCleanSourceCommit } from '../ui-coverage/policy.mjs';

const appRoot = fileURLToPath(new URL('../..', import.meta.url));
const repositoryRoot = resolve(appRoot, '../../..');
const sourceCommit = execFileSync('git', ['rev-parse', 'HEAD'], {
  cwd: repositoryRoot,
  encoding: 'utf8',
}).trim();
const workingTreeStatus = execFileSync('git', ['status', '--porcelain=v1', '--untracked-files=all'], {
  cwd: repositoryRoot,
  encoding: 'utf8',
}).trimEnd();
assertCleanSourceCommit({ sourceCommit, workingTreeStatus });

type RenderCatalog = {
  source: { sha256: string };
  renderProfile: { id: string; os: string; browser: string };
};
type GoldenApproval = {
  id: string;
  status: string;
  baseline: string;
  approvedBy?: string;
  approvedAt?: string;
  approvedSourceSha?: string;
  baselineSha256?: string;
};
type ApprovalDocument = {
  sourceInventorySha256: string;
  renderProfileId: string;
  scenarios: GoldenApproval[];
};
const catalog = JSON.parse(await readFile(new URL('../ui-coverage/catalog.json', import.meta.url), 'utf8')) as RenderCatalog;
const approvalDocument = JSON.parse(await readFile(new URL('../ui-coverage/golden-approvals.json', import.meta.url), 'utf8')) as ApprovalDocument;

for (const scene of visualScenes) {
  test(`approved visual baseline ${scene.id}`, async ({ page, context, browser }, testInfo) => {
    testInfo.annotations.push({ type: 'scenario-id', description: scene.id });
    expect(testInfo.config.updateSnapshots, 'Approved visual baselines must never be updated by this gate').toBe('none');
    const approval = approvalDocument.scenarios.find(candidate => candidate.id === scene.id);
    const baselinePath = approval ? resolve(appRoot, approval.baseline) : undefined;
    const relativeBaseline = baselinePath ? relative(appRoot, baselinePath) : '';
    const baselineIsInsideApp = Boolean(
      baselinePath &&
      !isAbsolute(relativeBaseline) &&
      relativeBaseline !== '..' &&
      !relativeBaseline.startsWith(`..${sep}`),
    );
    let baselineBytes: Buffer | undefined;
    if (baselineIsInsideApp) {
      try {
        baselineBytes = await readFile(baselinePath!);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw error;
      }
    }
    assertApprovedGolden({
      approval: approval && {
        ...approval,
        sourceInventorySha256: approvalDocument.sourceInventorySha256,
        renderProfileId: approvalDocument.renderProfileId,
      },
      sourceSha256: catalog.source.sha256,
      sourceCommit,
      renderProfileId: catalog.renderProfile.id,
      baselineExists: baselineBytes !== undefined,
      baselineSha256: baselineBytes && createHash('sha256').update(baselineBytes).digest('hex'),
    });
    expect(baselinePath).toBe(testInfo.snapshotPath(basename(approval!.baseline)));

    const sceneContext = scene.coarsePointer
      ? await browser.newContext({
          baseURL: captureOrigin,
          viewport: scene.viewport,
          deviceScaleFactor: 1,
          locale: 'en-US',
          timezoneId: 'UTC',
          colorScheme: scene.colorScheme,
          reducedMotion: 'reduce',
          hasTouch: true,
          isMobile: true,
          serviceWorkers: 'block',
        })
      : context;
    const scenePage = scene.coarsePointer ? await sceneContext.newPage() : page;
    try {
      const { violations, pageErrors } = await prepareVisualScene(sceneContext, scenePage, scene);
      await assertRenderingEnvironment(browser, scenePage, scene);
      if (scene.surface === 'app') await assertPinnedFonts(scenePage);
      await assertAccessibility(scenePage);
      assertNoScenarioNetworkViolations(violations, pageErrors);
      await expect(scenePage).toHaveScreenshot(basename(approval!.baseline), {
        animations: 'disabled',
        caret: 'hide',
        fullPage: true,
      });
    } finally {
      if (scene.coarsePointer) await sceneContext.close();
    }
  });
}
