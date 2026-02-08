import { describe, it, expect } from 'vitest';
import { PrintersPage } from '../PrintersPage';

describe('PrintersPage', () => {
  it('should export PrintersPage component', () => {
    expect(PrintersPage).toBeDefined();
    expect(typeof PrintersPage).toBe('function');
  });

  it('should be a React component', () => {
    expect(PrintersPage.name).toBe('PrintersPage');
  });
});

