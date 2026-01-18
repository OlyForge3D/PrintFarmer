import { test, expect } from '@playwright/test';

/**
 * Layout and Responsive Design Tests
 * 
 * These tests verify that the UI renders correctly across different
 * screen sizes and browsers, checking for:
 * - Element visibility
 * - No overlapping controls
 * - No clipped content
 * - Proper responsive behavior
 */

test.describe('Layout and Responsive Design', () => {
  test.describe('Page Structure', () => {
    test('should render main content area on all viewports', async ({ page }) => {
      const viewports = [
        { width: 1920, height: 1080, name: 'desktop' },
        { width: 768, height: 1024, name: 'tablet' },
        { width: 375, height: 667, name: 'mobile' },
      ];

      for (const vp of viewports) {
        await page.setViewportSize({ width: vp.width, height: vp.height });
        await page.goto('/');
        await page.waitForLoadState('networkidle');
        
        // Main content should be visible
        const main = page.locator('main, [role="main"], #root > div').first();
        await expect(main, `Main content not visible at ${vp.name}`).toBeVisible();
      }
    });

    test('should show sidebar or navigation on desktop', async ({ page }) => {
      await page.setViewportSize({ width: 1920, height: 1080 });
      await page.goto('/');
      await page.waitForLoadState('networkidle');
      
      // Look for sidebar or navigation elements (flexible selectors)
      const sidebarOrNav = page.locator('aside, nav, [role="navigation"], [data-sidebar], .sidebar, [class*="sidebar"]').first();
      const hasNav = await sidebarOrNav.isVisible().catch(() => false);
      
      // Or look for any navigation links
      const navLinks = page.locator('a[href^="/"]');
      const linkCount = await navLinks.count();
      
      // Either sidebar is visible OR there are navigation links
      expect(hasNav || linkCount > 0, 'No navigation found').toBeTruthy();
    });
  });

  test.describe('Page Content', () => {
    test('should not have horizontally scrolling content at standard widths', async ({ page }) => {
      const viewports = [
        { width: 1920, height: 1080, name: 'desktop-large' },
        { width: 1366, height: 768, name: 'desktop-medium' },
        { width: 1024, height: 768, name: 'desktop-small' },
        { width: 768, height: 1024, name: 'tablet' },
        { width: 375, height: 667, name: 'mobile' },
      ];

      for (const vp of viewports) {
        await page.setViewportSize({ width: vp.width, height: vp.height });
        await page.goto('/');
        await page.waitForLoadState('networkidle');

        // Check if body has horizontal overflow
        const hasHorizontalScroll = await page.evaluate(() => {
          return document.documentElement.scrollWidth > document.documentElement.clientWidth;
        });

        expect(hasHorizontalScroll, 
          `Unexpected horizontal scroll at ${vp.name} (${vp.width}x${vp.height})`
        ).toBeFalsy();
      }
    });

    test('should have readable text at all viewport sizes', async ({ page }) => {
      const viewports = [
        { width: 1920, height: 1080 },
        { width: 375, height: 667 },
      ];

      for (const vp of viewports) {
        await page.setViewportSize(vp);
        await page.goto('/');
        await page.waitForLoadState('networkidle');

        // Check main text elements have reasonable font size
        const textElements = page.locator('p, span, h1, h2, h3, h4, h5, h6, label, button');
        const count = await textElements.count();

        for (let i = 0; i < Math.min(count, 20); i++) {
          const element = textElements.nth(i);
          if (await element.isVisible()) {
            const fontSize = await element.evaluate(el => 
              parseFloat(getComputedStyle(el).fontSize)
            );
            // Font should be at least 10px for readability
            expect(fontSize).toBeGreaterThanOrEqual(10);
          }
        }
      }
    });
  });

  test.describe('Control Visibility', () => {
    test('buttons should be fully visible and not clipped', async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('networkidle');

      const buttons = page.locator('button:visible');
      const count = await buttons.count();

      for (let i = 0; i < count; i++) {
        const button = buttons.nth(i);
        const box = await button.boundingBox();
        
        if (box) {
          const viewport = page.viewportSize();
          if (viewport) {
            // Button should be within viewport or scrollable area
            expect(box.x).toBeGreaterThanOrEqual(-1); // Allow 1px tolerance
            expect(box.y).toBeGreaterThanOrEqual(-1);
            
            // Check button is not clipped (width and height should be reasonable)
            expect(box.width).toBeGreaterThan(10);
            expect(box.height).toBeGreaterThan(10);
          }
        }
      }
    });

    test('form inputs should be accessible and properly sized', async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('networkidle');

      const inputs = page.locator('input:visible, select:visible, textarea:visible');
      const count = await inputs.count();

      for (let i = 0; i < count; i++) {
        const input = inputs.nth(i);
        const box = await input.boundingBox();
        
        if (box) {
          // Inputs should have minimum touch target size (44x44 is WCAG recommendation)
          // We'll be lenient and check for at least 24px
          expect(box.height, `Input ${i} height too small`).toBeGreaterThanOrEqual(24);
          expect(box.width, `Input ${i} width too small`).toBeGreaterThanOrEqual(40);
        }
      }
    });
  });

  test.describe('Element Overlap Detection', () => {
    test('interactive elements should not overlap significantly', async ({ page }) => {
      await page.goto('/');
      await page.waitForLoadState('networkidle');

      // Get all clickable elements
      const clickables = page.locator('button:visible, a:visible, [role="button"]:visible, input[type="submit"]:visible');
      const count = await clickables.count();
      const boxes: { index: number; box: { x: number; y: number; width: number; height: number } }[] = [];

      // Collect bounding boxes
      for (let i = 0; i < count; i++) {
        const element = clickables.nth(i);
        const box = await element.boundingBox();
        if (box && box.width > 0 && box.height > 0) {
          boxes.push({ index: i, box });
        }
      }

      // Check for overlaps
      const significantOverlaps: string[] = [];
      for (let i = 0; i < boxes.length; i++) {
        for (let j = i + 1; j < boxes.length; j++) {
          const a = boxes[i].box;
          const b = boxes[j].box;

          // Check if boxes overlap
          const overlapX = Math.max(0, Math.min(a.x + a.width, b.x + b.width) - Math.max(a.x, b.x));
          const overlapY = Math.max(0, Math.min(a.y + a.height, b.y + b.height) - Math.max(a.y, b.y));
          
          // If overlap area is significant (more than 50% of smaller element)
          if (overlapX > 0 && overlapY > 0) {
            const overlapArea = overlapX * overlapY;
            const smallerArea = Math.min(a.width * a.height, b.width * b.height);
            
            if (overlapArea / smallerArea > 0.5) {
              significantOverlaps.push(`Elements ${boxes[i].index} and ${boxes[j].index} overlap by ${Math.round(overlapArea / smallerArea * 100)}%`);
            }
          }
        }
      }

      expect(significantOverlaps, 
        `Found ${significantOverlaps.length} significantly overlapping interactive elements: ${significantOverlaps.join(', ')}`
      ).toHaveLength(0);
    });
  });

  test.describe('Responsive Breakpoints', () => {
    test('layout should adapt at common breakpoints', async ({ page }) => {
      const breakpoints = [
        { width: 1920, height: 1080, name: 'xl' },
        { width: 1280, height: 720, name: 'lg' },
        { width: 1024, height: 768, name: 'md' },
        { width: 768, height: 1024, name: 'sm' },
        { width: 375, height: 667, name: 'xs' },
      ];

      for (const bp of breakpoints) {
        await page.setViewportSize({ width: bp.width, height: bp.height });
        await page.goto('/');
        await page.waitForLoadState('networkidle');
        
        // Page should render without errors
        const errors: string[] = [];
        page.on('pageerror', error => errors.push(error.message));
        
        await page.waitForTimeout(500);
        
        // Take screenshot for visual verification
        await page.screenshot({ 
          path: `test-results/breakpoint-${bp.name}-${bp.width}x${bp.height}.png` 
        });
        
        // Check no critical JS errors
        const criticalErrors = errors.filter(e => 
          !e.includes('ResizeObserver') && 
          !e.includes('Network Error')
        );
        expect(criticalErrors).toHaveLength(0);
      }
    });
  });
});
