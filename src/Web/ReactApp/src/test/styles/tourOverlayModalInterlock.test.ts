import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const tourThemeCss = readFileSync(
  resolve(__dirname, '../../styles/tour-theme.css'),
  'utf8',
);

describe('tour overlay/modal interlock (#2621)', () => {
  it('suspends tour hit testing while any app modal dialog is open', () => {
    expect(tourThemeCss).toContain(
      'body:has([role="dialog"][aria-modal="true"]) .driver-overlay,',
    );
    expect(tourThemeCss).toContain(
      'body:has([role="dialog"][aria-modal="true"]) .driver-popover {',
    );
    expect(tourThemeCss).toContain('pointer-events: none !important;');
  });
});
