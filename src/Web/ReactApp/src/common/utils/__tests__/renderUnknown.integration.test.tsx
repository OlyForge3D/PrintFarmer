import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import renderUnknown from '@/common/utils/renderUnknown';

describe('renderUnknown util', () => {
  it('renders JSON for object values', () => {
    const obj = { foo: 'bar' };
    // renderUnknown returns a React node; wrap it in a fragment for rendering
    render(<>{renderUnknown(obj)}</>);
    expect(screen.getByText(/foo/)).toBeTruthy();
    expect(screen.getByText(/bar/)).toBeTruthy();
  });
});
