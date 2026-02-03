import { describe, it, expect } from 'vitest';
import { ApiKeysPage } from '../ApiKeysPage';

describe('ApiKeysPage', () => {
  it('should export ApiKeysPage component', () => {
    expect(ApiKeysPage).toBeDefined();
    expect(typeof ApiKeysPage).toBe('function');
  });

  it('should be a React component', () => {
    expect(ApiKeysPage.name).toBe('ApiKeysPage');
  });
});
