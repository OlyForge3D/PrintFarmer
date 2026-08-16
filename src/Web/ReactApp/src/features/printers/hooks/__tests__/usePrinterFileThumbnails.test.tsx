import { renderHook, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { PrinterFileDto } from '@/types/api';

const hoisted = vi.hoisted(() => ({
  getPrinterFileThumbnail: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: { getPrinterFileThumbnail: hoisted.getPrinterFileThumbnail },
}));

import { usePrinterFileThumbnails } from '../usePrinterFileThumbnails';

function makeFile(overrides: Partial<PrinterFileDto> = {}): PrinterFileDto {
  return {
    fileName: 'benchy.gcode',
    ...overrides,
  } as PrinterFileDto;
}

describe('usePrinterFileThumbnails (#1650)', () => {
  beforeEach(() => {
    hoisted.getPrinterFileThumbnail.mockReset();
    URL.createObjectURL = vi.fn(() => 'blob:mock-object-url');
    URL.revokeObjectURL = vi.fn();
  });

  it('does not call the API when no file has a thumbnailUrl', async () => {
    const files = [makeFile({ thumbnailUrl: undefined })];

    const { result } = renderHook(() => usePrinterFileThumbnails(files));

    await waitFor(() => {
      expect(result.current.objectUrls).toEqual({});
    });
    expect(hoisted.getPrinterFileThumbnail).not.toHaveBeenCalled();
  });

  it('fetches each unique thumbnailUrl once as an authenticated blob and exposes an object URL', async () => {
    hoisted.getPrinterFileThumbnail.mockResolvedValue(new Blob(['fake-png-bytes']));
    const files = [
      makeFile({
        fileName: 'benchy.gcode',
        thumbnailUrl: '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fbenchy-300x300.png',
      }),
    ];

    const { result } = renderHook(() => usePrinterFileThumbnails(files));

    await waitFor(() => {
      expect(result.current.objectUrls[files[0].thumbnailUrl!]).toBe('blob:mock-object-url');
    });

    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(1);
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledWith(
      files[0].thumbnailUrl,
      expect.any(AbortSignal)
    );
    expect(result.current.failed).toEqual({});
  });

  it('deduplicates identical thumbnailUrl values across multiple files into a single fetch', async () => {
    hoisted.getPrinterFileThumbnail.mockResolvedValue(new Blob(['fake-png-bytes']));
    const sharedUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fshared.png';
    const files = [
      makeFile({ fileName: 'a.gcode', thumbnailUrl: sharedUrl }),
      makeFile({ fileName: 'b.gcode', thumbnailUrl: sharedUrl }),
    ];

    const { result } = renderHook(() => usePrinterFileThumbnails(files));

    await waitFor(() => {
      expect(result.current.objectUrls[sharedUrl]).toBe('blob:mock-object-url');
    });

    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(1);
  });

  it('marks a thumbnailUrl as failed instead of throwing when the fetch rejects', async () => {
    hoisted.getPrinterFileThumbnail.mockRejectedValueOnce(new Error('network error'));
    const files = [
      makeFile({
        thumbnailUrl: '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fbroken.png',
      }),
    ];

    const { result } = renderHook(() => usePrinterFileThumbnails(files));

    await waitFor(() => {
      expect(result.current.failed[files[0].thumbnailUrl!]).toBe(true);
    });
    expect(result.current.objectUrls).toEqual({});
  });

  it('revokes previously created object URLs when the file list changes', async () => {
    hoisted.getPrinterFileThumbnail.mockResolvedValue(new Blob(['fake-png-bytes']));
    const firstUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Ffirst.png';
    const secondUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fsecond.png';

    const { result, rerender } = renderHook(
      ({ files }: { files: PrinterFileDto[] }) => usePrinterFileThumbnails(files),
      { initialProps: { files: [makeFile({ thumbnailUrl: firstUrl })] } }
    );

    await waitFor(() => {
      expect(result.current.objectUrls[firstUrl]).toBe('blob:mock-object-url');
    });

    rerender({ files: [makeFile({ thumbnailUrl: secondUrl })] });

    await waitFor(() => {
      expect(result.current.objectUrls[secondUrl]).toBe('blob:mock-object-url');
    });
    expect(result.current.objectUrls[firstUrl]).toBeUndefined();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:mock-object-url');
  });
});
