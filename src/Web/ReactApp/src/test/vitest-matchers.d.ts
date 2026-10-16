import type { TestingLibraryMatchers } from '@testing-library/jest-dom/matchers';
import type { expect } from 'vitest';

declare module 'vitest' {
  // jest-dom 7's Vitest augmentation still uses the pre-v5 Assertion<T> contract.
  // eslint-disable-next-line @typescript-eslint/no-empty-object-type
  interface Matchers<R extends void | Promise<void> = void | Promise<void>>
    extends TestingLibraryMatchers<ReturnType<typeof expect.stringContaining>, R> {}
}
