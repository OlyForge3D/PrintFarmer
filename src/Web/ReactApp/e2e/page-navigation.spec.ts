import { test, expect } from '@playwright/test';

/**
 * Page Navigation Tests
 * 
 * Tests that verify all main pages load correctly and can be navigated to
 * across different browsers.
 */

test.describe('Page Navigation', () => {
  const pages = [
    { path: '/', name: 'Home/Dashboard' },
    { path: '/printers', name: 'Printers' },
    { path: '/catalog', name: 'Catalog' },
    { path: '/models', name: '3D Models' },
    { path: '/queue', name: 'Print Queue' },
    { path: '/files', name: 'Files' },
  ];

  for (const pageConfig of pages) {
    test(`should load ${pageConfig.name} page`, async ({ page }) => {
      const response = await page.goto(pageConfig.path);
      
      // Page should load successfully
      expect(response?.status()).toBeLessThan(400);
      
      // Page should have content
      await expect(page.locator('body')).not.toBeEmpty();
      
      // No JavaScript errors
      const errors: string[] = [];
      page.on('pageerror', error => errors.push(error.message));
      
      await page.waitForLoadState('networkidle');
      
      // Allow some time for any async errors
      await page.waitForTimeout(500);
      
      // Filter out known acceptable errors
      const criticalErrors = errors.filter(e => 
        !e.includes('ResizeObserver') && 
        !e.includes('Network Error') &&
        !e.includes('Failed to fetch')
      );
      
      expect(criticalErrors, `JS errors on ${pageConfig.name}: ${criticalErrors.join(', ')}`).toHaveLength(0);
    });
  }

  test('should navigate between pages using sidebar', async ({ page }) => {
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    
    // Look for navigation links
    const navLinks = page.locator('nav a, [role="navigation"] a, aside a');
    const count = await navLinks.count();
    
    if (count > 0) {
      // Click each nav link and verify navigation works
      for (let i = 0; i < Math.min(count, 5); i++) {
        const link = navLinks.nth(i);
        if (await link.isVisible()) {
          const href = await link.getAttribute('href');
          if (href && href.startsWith('/') && !href.includes('http')) {
            await link.click();
            await page.waitForLoadState('networkidle');
            
            // URL should change
            expect(page.url()).toContain(href);
          }
        }
      }
    }
  });

  test('should handle browser back/forward navigation', async ({ page }) => {
    // Navigate to first page (may redirect to login)
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    const firstUrl = page.url();
    
    // Navigate to second page
    await page.goto('/catalog');
    await page.waitForLoadState('networkidle');
    const secondUrl = page.url();
    
    // Only test if URLs are different (not both redirecting to login)
    if (firstUrl !== secondUrl) {
      // Go back
      await page.goBack();
      await page.waitForLoadState('networkidle');
      expect(page.url()).toBe(firstUrl);
      
      // Go forward
      await page.goForward();
      await page.waitForLoadState('networkidle');
      expect(page.url()).toBe(secondUrl);
    } else {
      // App redirects all routes to login - test back/forward on login page
      await page.goto('/login');
      await page.waitForLoadState('networkidle');
      
      await page.goto('/login?redirect=/printers');
      await page.waitForLoadState('networkidle');
      
      await page.goBack();
      await page.waitForLoadState('networkidle');
      // Just verify we can navigate back without errors
      expect(page.url()).toContain('/login');
    }
  });
});

test.describe('Page Loading Performance', () => {
  test('pages should load within acceptable time', async ({ page }) => {
    const pages = ['/', '/printers', '/catalog', '/models'];
    
    for (const path of pages) {
      const startTime = Date.now();
      await page.goto(path);
      await page.waitForLoadState('domcontentloaded');
      const loadTime = Date.now() - startTime;
      
      // Page should load within 10 seconds (generous for CI)
      expect(loadTime, `${path} took ${loadTime}ms to load`).toBeLessThan(10000);
    }
  });
});
