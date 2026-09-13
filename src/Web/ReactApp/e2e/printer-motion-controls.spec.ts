import { expect, test, type Page } from '@playwright/test';

// This page imports real controls but never mounts the application or contacts a printer.
async function mountMotionFixture(page: Page, state: 'Queued' | 'Running' | 'Unknown' = 'Queued') {
  await page.route('**/api/**', route => route.fulfill({ status: 503, json: { message: 'No live API in motion fixtures' } }));
  await page.route('**/__motion_fixture', route => route.fulfill({
    contentType: 'text/html',
    body: '<!doctype html><html><head><title>Motion controls fixture</title></head><body><main id="motion-root"></main></body></html>',
  }));
  await page.goto('/__motion_fixture');
  await page.addScriptTag({
    type: 'module',
    content: `
      import RefreshRuntime from '/@react-refresh';
      RefreshRuntime.injectIntoGlobalHook(window);
      window.$RefreshReg$ = () => {};
      window.$RefreshSig$ = () => type => type;
      window.__vite_plugin_react_preamble_installed__ = true;
      const { default: React } = await import('/node_modules/.vite/deps/react.js');
      const { default: ReactDom } = await import('/node_modules/.vite/deps/react-dom_client.js');
      const { PrinterControlOperationPanel } = await import('/src/features/printers/components/PrinterControlOperationPanel.tsx');
      await import('/src/index.css');
      const operation = {
        operationId: '22222222-2222-4222-8222-222222222222',
        printerId: '11111111-1111-4111-8111-111111111111',
        kind: 'Jog', state: ${JSON.stringify(state)}, rowVersion: 'fixture-v1',
        requiresRecovery: ${state === 'Unknown'}, barrierHeld: true,
        completionEvidence: 'None', senderIsolation: 'NotRequested', failure: null,
      };
      const control = {
        isMoonraker: true, blocked: true, checking: false, submitting: false,
        admitting: false, uncertain: false, error: null, saved: null, operation,
        missingAdmission: false, canRecover: false, canRetryAdmission: false, etag: '"fixture-v1"',
        current: { operation, physicalControl: { barrierHeld: true, supportedOperations: ['Jog'], requiresRecovery: operation.requiresRecovery } },
        tracker: { refresh: async () => operation },
      };
      ReactDom.createRoot(document.getElementById('motion-root')).render(
        React.createElement('div', { style: { maxWidth: '420px', padding: '12px' } },
          React.createElement(PrinterControlOperationPanel, { control }))
      );
    `,
  });
  await expect(page.getByRole('region', { name: 'Motion status' })).toBeVisible();
}

test.describe('Printer motion feedback without physical commands', () => {
  test('routine progress keeps diagnostics collapsed and keyboard-accessible', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await mountMotionFixture(page);
    await expect(page.getByRole('status')).toHaveText('Motion: Jog: waiting to start');
    await expect(page.getByText(/Do not repeat/)).toHaveCount(0);
    const operationId = page.getByText('Operation: 22222222-2222-4222-8222-222222222222');
    await expect(operationId).not.toBeVisible();
    const details = page.getByText('Motion technical details', { exact: true });
    await details.focus();
    await page.keyboard.press('Enter');
    await expect(operationId).toBeVisible();
    await expect(page.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(operationId).not.toBeVisible();
    expect(errors).toEqual([]);
  });

  test('unknown outcomes retain visible warnings and permission-aware recovery guidance', async ({ page }) => {
    await mountMotionFixture(page, 'Unknown');
    await expect(page.getByRole('status')).toHaveText('Motion: Jog: outcome unknown');
    await expect(page.getByText(/Do not repeat this movement/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Recheck motion status' })).toBeVisible();
    await expect(page.getByText(/Recovery requires queue:reconcile/)).toBeVisible();
    await expect(page.getByRole('button', { name: /Request recovery/ })).toHaveCount(0);
  });
});
