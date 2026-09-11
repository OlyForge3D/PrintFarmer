import { afterEach, describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const tourThemeCss = readFileSync(
  resolve(__dirname, '../../styles/tour-theme.css'),
  'utf8',
);

function mountTourThemeFixture(html: string) {
  document.head.innerHTML = '';
  document.body.innerHTML = html;

  const style = document.createElement('style');
  style.textContent = tourThemeCss;
  document.head.appendChild(style);

  return {
    overlay: document.querySelector<HTMLElement>('.driver-overlay'),
    popover: document.querySelector<HTMLElement>('.driver-popover'),
  };
}

afterEach(() => {
  document.head.innerHTML = '';
  document.body.innerHTML = '';
});

describe('tour overlay/modal interlock (#2621)', () => {
  it('does not suspend tour hit testing for an inert mobile drawer shell', () => {
    const { overlay, popover } = mountTourThemeFixture(`
      <div class="driver-overlay"></div>
      <div class="driver-popover pf-tour-popover"></div>
      <div role="dialog" aria-modal="true" inert></div>
    `);

    expect(overlay).not.toBeNull();
    expect(popover).not.toBeNull();
    expect(getComputedStyle(overlay!).pointerEvents).toBe('auto');
    expect(getComputedStyle(popover!).pointerEvents).toBe('auto');
  });

  it('suspends tour hit testing while a real modal dialog is open', () => {
    const { overlay, popover } = mountTourThemeFixture(`
      <div class="driver-overlay"></div>
      <div class="driver-popover pf-tour-popover"></div>
      <div role="dialog" aria-modal="true"></div>
    `);

    expect(overlay).not.toBeNull();
    expect(popover).not.toBeNull();
    expect(getComputedStyle(overlay!).pointerEvents).toBe('none');
    expect(getComputedStyle(popover!).pointerEvents).toBe('none');
  });

  it('requires non-inert dialogs in the selector contract', () => {
    expect(tourThemeCss).toContain(
      'body:has([role="dialog"][aria-modal="true"]:not([inert])) .driver-overlay,',
    );
    expect(tourThemeCss).toContain(
      'body:has([role="dialog"][aria-modal="true"]:not([inert])) .driver-popover {',
    );
    expect(tourThemeCss).not.toContain(
      'body:has([role="dialog"][aria-modal="true"]) .driver-overlay,',
    );
    expect(tourThemeCss).toContain('pointer-events: none !important;');
  });
});
