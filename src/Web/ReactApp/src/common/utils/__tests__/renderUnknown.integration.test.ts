import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import renderUnknown from '@/common/utils/renderUnknown';

describe('renderUnknown util', () => {
  it('renders JSON for object values', () => {
    const obj = { foo: 'bar' };
    // renderUnknown returns a React node; pass it directly to render
    render(renderUnknown(obj) as any);
    expect(screen.getByText(/foo/)).toBeTruthy();
    expect(screen.getByText(/bar/)).toBeTruthy();
  });
});
