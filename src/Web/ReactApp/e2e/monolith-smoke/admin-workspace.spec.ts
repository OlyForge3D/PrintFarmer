/**
 * Monolith smoke journey (issue #2507): integrated admin workspace routing.
 *
 * This is intentionally non-destructive. It proves the completed admin
 * redesign's real browser routes stitch together under the existing monolith
 * fixture: the attention-first hub is reachable, the worker jobs URL is owned
 * by WorkerManagementPage's `workerTab`, standalone Power Monitors remains a
 * configuration destination outside the settings shell, and personal settings
 * stays separate from the category-titled admin settings workspace.
 */
import { test } from './fixtures/monolith-setup';
import { assertAdminWorkspaceBoundaries } from '../fixtures/admin-workspace-journey';

test.describe('monolith smoke: admin workspace', () => {
  test('admin workspace preserves operations, standalone configuration, and personal settings boundaries', async ({ page }) => {
    await assertAdminWorkspaceBoundaries(page);
  });
});
