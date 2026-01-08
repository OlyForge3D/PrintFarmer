/* eslint-disable */
// @ts-nocheck
/**
 * Codemod test fixture - input for wrap-console-and-renderunknown codemod.
 * This file demonstrates code that needs transformation for testing purposes only.
 * TypeScript and linting checks are intentionally disabled as this is a test fixture.
 */
import React from 'react';

export const Example = ({ obj }: { obj: unknown }) => {
  console.debug('raw object', obj);
  console.info('info message');
  return (
    <div>
      <pre>{JSON.stringify(obj, null, 2)}</pre>
      <span>{JSON.stringify(obj)}</span>
    </div>
  );
};
