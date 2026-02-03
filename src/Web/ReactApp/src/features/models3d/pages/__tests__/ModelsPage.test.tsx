import { describe, it, expect } from 'vitest';
import { ModelsPage } from '../ModelsPage';

describe('ModelsPage', () => {
  it('should export ModelsPage component', () => {
    expect(ModelsPage).toBeDefined();
    expect(typeof ModelsPage).toBe('function');
  });

  it('should be a React component', () => {
    expect(ModelsPage.name).toBe('ModelsPage');
  });
});
