/* eslint-disable */
// @ts-nocheck
/**
 * Codemod test fixture - expected output from wrap-console-and-renderunknown codemod.
 * This file demonstrates the transformation for testing purposes only.
 * TypeScript and linting checks are intentionally disabled as this is a test fixture.
 */
import React from 'react';
import { renderUnknown } from '@/utils/renderUnknown';

/* eslint-disable local/pf-no-unguarded-console */
export const Example = ({ obj }: { obj: unknown }) => {
  if (window.PrintFarmerDebug?.toolsCodemodTest) {
    try { console.debug('raw object', obj); } catch { /* ignore debug stringify errors */ }
  }
  if (window.PrintFarmerDebug?.toolsCodemodTest) {
    try { console.info('info message'); } catch { /* ignore debug stringify errors */ }
  }
  return (
    <div>
      <pre>{renderUnknown(obj)}</pre>
      <span>{renderUnknown(obj)}</span>
    </div>
  );
};
