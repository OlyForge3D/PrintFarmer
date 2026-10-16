import { describe, expect, expectTypeOf, it } from 'vitest';

describe('jest-dom matcher compatibility', () => {
  it('preserves matcher arguments and synchronous return types', () => {
    const element = document.createElement('button');
    element.setAttribute('aria-label', 'Print');
    document.body.appendChild(element);

    try {
      const result = expect(element).toBeInTheDocument();
      expectTypeOf(result).toEqualTypeOf<void>();
      expectTypeOf(expect(element).toHaveAttribute).parameter(0).toEqualTypeOf<string>();
      expect(element).toHaveAccessibleName(expect.stringContaining('Print'));
      expect(element).not.toBeDisabled();
      expect(() => expect(null).toBeInTheDocument()).toThrow();
    } finally {
      element.remove();
    }
  });

  it('preserves asynchronous matcher return types', async () => {
    const result = expect(Promise.resolve(document.createElement('button')))
      .resolves.not.toBeDisabled();

    expectTypeOf(result).toEqualTypeOf<Promise<void>>();
    await result;
  });
});
