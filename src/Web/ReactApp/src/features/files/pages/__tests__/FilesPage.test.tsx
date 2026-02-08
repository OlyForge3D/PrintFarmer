import { describe, it, expect } from 'vitest';
import { FilesPage } from '../FilesPage';

describe('FilesPage', () => {
  it('should export FilesPage component', () => {
    expect(FilesPage).toBeDefined();
    expect(typeof FilesPage).toBe('function');
  });

  it('should be a React component', () => {
    expect(FilesPage.name).toBe('FilesPage');
  });
});
