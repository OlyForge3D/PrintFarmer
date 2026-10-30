import { expect, type Locator } from '@playwright/test';

export async function measureToolbar(toolbar: Locator) {
  return toolbar.locator('button, input:not([type=checkbox],[type=radio]), select').evaluateAll(elements =>
    elements.filter(element => element.getBoundingClientRect().width > 0).map(element => {
      const box = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      return {
        name: element.getAttribute('aria-label') || element.getAttribute('title')
          || ((element instanceof HTMLInputElement || element instanceof HTMLSelectElement)
            ? Array.from(element.labels ?? []).map(label => label.textContent?.trim()).join(' ')
            : element.textContent?.trim()),
        tag: element.tagName,
        interiorSegment: element.parentElement?.classList.contains('pf-toolbar__group--segmented')
          && element !== element.parentElement.firstElementChild,
        height: box.height,
        radius: Number.parseFloat(style.borderTopLeftRadius),
        left: box.left, right: box.right, top: box.top, bottom: box.bottom,
        clipped: element.scrollWidth > element.clientWidth + 1 || element.scrollHeight > element.clientHeight + 1,
      };
    }));
}

export async function expectToolbarGeometry(toolbar: Locator, minimum: number, zoom = false) {
  const measurements = await measureToolbar(toolbar);
  expect(measurements.length, 'fixture must contain real controls').toBeGreaterThan(0);
  const box = await toolbar.boundingBox();
  for (const control of measurements) {
    if (zoom) expect(control.height, `${control.name} minimum height`).toBeGreaterThanOrEqual(minimum - 1);
    else expect(Math.abs(control.height - minimum), `${control.name} height: ${control.height}`).toBeLessThanOrEqual(1);
    expect(control.left, `${control.name} left boundary`).toBeGreaterThanOrEqual(box!.x - 1);
    expect(control.right, `${control.name} right boundary`).toBeLessThanOrEqual(box!.x + box!.width + 1);
    expect(control.clipped, `${control.name} text clipping`).toBe(false);
    const expectedRadius = control.tag === 'INPUT' || control.tag === 'SELECT' ? 4 : control.interiorSegment ? 0 : 2;
    expect(Math.abs(control.radius - expectedRadius), `${control.name} radius`).toBeLessThanOrEqual(1);
  }
  for (const control of await toolbar.locator('button:visible, input:visible:not([type=checkbox],[type=radio]), select:visible').all()) {
    await expect(control, 'every visible control needs a computed accessible name').toHaveAccessibleName(/\S/);
  }
  return measurements;
}
