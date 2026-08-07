import { describe, expect, it, vi } from 'vitest';

const extractor = vi.hoisted(() => ({
  moduleLoad: vi.fn(),
  isZipFile: vi.fn(),
  extractOrcaBundle: vi.fn(),
}));

vi.mock('@/features/slicer/orca/utils/orcaBundleExtractor', () => {
  extractor.moduleLoad();
  return {
    isZipFile: extractor.isZipFile,
    extractOrcaBundle: extractor.extractOrcaBundle,
  };
});

import { readOrcaBundle } from '../orcaBundleLoader';

describe('readOrcaBundle lazy vendor boundary', () => {
  it('loads the archive implementation only when a bundle is read', async () => {
    expect(extractor.moduleLoad).not.toHaveBeenCalled();

    const invalidBuffer = new ArrayBuffer(4);
    extractor.isZipFile.mockReturnValueOnce(false);
    await expect(readOrcaBundle(invalidBuffer)).resolves.toBeNull();
    expect(extractor.moduleLoad).toHaveBeenCalledTimes(1);
    expect(extractor.extractOrcaBundle).not.toHaveBeenCalled();

    const validBuffer = new ArrayBuffer(8);
    extractor.isZipFile.mockReturnValueOnce(true);
    extractor.extractOrcaBundle.mockResolvedValueOnce('{"printer":[]}');
    await expect(readOrcaBundle(validBuffer)).resolves.toBe('{"printer":[]}');
    expect(extractor.extractOrcaBundle).toHaveBeenCalledWith(validBuffer);
  });
});
