import { expect, test } from '@playwright/test';
import { captureTime, isolateCapture, printers } from './fixtures';
import { PrinterBackend, type PrinterBackendCapabilitiesDto } from '../../src/types/api';

for (const width of [1440, 390]) {
  for (const surface of ['detail', 'sidebar']) {
    test(`unconfirmed motion help on ${surface} at ${width}px`, async ({ page, context }, testInfo) => {
      const isolation = await isolateCapture(context);
      const printer = { ...printers[7], homedAxes: 'xyz' };
      const capabilities: PrinterBackendCapabilitiesDto = {
        printerId: printer.id, printerName: printer.name, backend: PrinterBackend.PrusaLink,
        supportsCamera: false, supportsFileDownload: false, supportsFileList: false,
        supportsFileUpload: false, supportsStartPrint: false, supportsControlOperations: false,
        supportsFileMetadata: false, supportsMovement: false, supportsRelativeMovement: false,
        supportsAbsoluteMovement: false, supportsHoming: false, supportsHomingXY: false,
        supportsHomingZ: false, supportsTemperatureControl: false, supportsPrinterInformation: false,
        supportsHistory: false, supportsFilamentControl: false, supportsObjectExclusion: false,
      };
      await context.route('**/api/printers', route => route.fulfill({ json: [printer] }));
      await context.route('**/api/printers/backend-capabilities', route => route.fulfill({ json: [capabilities] }));
      await context.route(`**/api/printers/${printer.id}`, route => route.fulfill({ json: printer }));
      await context.route(`**/api/printers/${printer.id}/details`, route => route.fulfill({ json: {
        ...printer, toolheads: [], capabilities,
      } }));
      await context.route(`**/api/printers/${printer.id}/version`, route => route.fulfill({ json: {
        printerId: printer.id, backend: printer.backend, supported: false,
      } }));
      await context.route(`**/api/maintenance/printers/${printer.id}/statistics`, route => route.fulfill({ json: {} }));
      await page.setViewportSize({ width, height: 1100 });
      await page.clock.setFixedTime(new Date(captureTime));
      await page.goto(surface === 'detail' ? '/printers?view=detailed' : `/printers/${printer.id}?view=collapsed`);
      const movement = page.getByRole('button', { name: 'Movement', exact: true });
      if (await movement.count() && await movement.getAttribute('aria-expanded') === 'false') await movement.click();
      const help = page.getByRole('status').filter({ hasText: 'API motion support has not been confirmed' });
      await expect(help).toBeVisible();
      await expect(help).toContainText('reload this page to check again');
      await expect(page.getByRole('button', { name: 'Home all axes' })).toBeDisabled();
      await expect(page.getByRole('button', { name: 'Jog X positive' })).toBeDisabled();
      await help.scrollIntoViewIfNeeded();
      const overflow = await help.evaluate(element => element.scrollWidth > element.clientWidth);
      expect(overflow).toBe(false);
      await page.screenshot({ path: testInfo.outputPath(`motion-help-${surface}-${width}.png`), fullPage: true });
      expect(isolation.violations).toEqual([]);
    });
  }
}
