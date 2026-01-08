import React from 'react';
import { describe, it, expect } from 'vitest';
import { render } from '@testing-library/react';
import { renderUnknown } from '@/common/utils/renderUnknown';

describe('renderUnknown', () => {
  it('renders null/undefined as null', () => {
    expect(renderUnknown(null)).toBeNull();
    expect(renderUnknown(undefined)).toBeNull();
  });

  it('renders primitives as strings', () => {
    expect(renderUnknown('hello')).toBe('hello');
    expect(renderUnknown(42)).toBe('42');
    expect(renderUnknown(true)).toBe('true');
  });

  it('renders React elements unchanged', () => {
    const el = <div data-testid="x">Hi</div>;
    const result = renderUnknown(el) as React.ReactElement;
    const { getByTestId } = render(result);
    expect(getByTestId('x')).toBeTruthy();
  });

  it('renders objects/arrays as pretty JSON inside a pre', () => {
    const obj = { a: 1, b: 'two' };
    const node = renderUnknown(obj) as React.ReactElement;
    const { container } = render(node);
    expect(container.querySelector('pre')).toBeTruthy();
    expect(container.textContent).toContain('"a": 1');
  });
});
