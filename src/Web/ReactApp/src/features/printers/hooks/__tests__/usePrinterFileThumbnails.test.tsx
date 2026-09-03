import { StrictMode } from 'react';
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

  it('#2393: does not re-fetch or revoke already-resolved thumbnails when the file list grows (visible-row subset expanding)', async () => {
    // Regression coverage for PrinterFilesModal passing an incrementally growing
    // visible-file subset (via IntersectionObserver) instead of a stable full list: adding
    // a file to the end of the list must not re-fetch or revoke thumbnails already resolved
    // for files still present in the new list.
    hoisted.getPrinterFileThumbnail.mockResolvedValue(new Blob(['fake-png-bytes']));
    const firstUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Ffirst.png';
    const secondUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fsecond.png';

    const { result, rerender } = renderHook(
      ({ files }: { files: PrinterFileDto[] }) => usePrinterFileThumbnails(files),
      { initialProps: { files: [makeFile({ fileName: 'a.gcode', thumbnailUrl: firstUrl })] } }
    );

    await waitFor(() => {
      expect(result.current.objectUrls[firstUrl]).toBe('blob:mock-object-url');
    });
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(1);

    rerender({
      files: [
        makeFile({ fileName: 'a.gcode', thumbnailUrl: firstUrl }),
        makeFile({ fileName: 'b.gcode', thumbnailUrl: secondUrl }),
      ],
    });

    await waitFor(() => {
      expect(result.current.objectUrls[secondUrl]).toBe('blob:mock-object-url');
    });

    // Only the newly-added url should have triggered a fetch; the already-resolved
    // firstUrl must neither be re-fetched nor revoked.
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(2);
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledWith(secondUrl, expect.any(AbortSignal));
    expect(result.current.objectUrls[firstUrl]).toBe('blob:mock-object-url');
    expect(URL.revokeObjectURL).not.toHaveBeenCalled();
  });

  it('#2393: does not abort or restart an in-flight thumbnail fetch when the file list grows before it resolves', async () => {
    // Regression coverage for the overlapping-effect-run race: if a still-in-flight fetch
    // from a prior render were aborted whenever the effect re-ran (e.g. continuous scroll
    // adding a new visible row before the previous batch finished), that thumbnail could be
    // repeatedly restarted and never resolve.
    const firstUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Ffirst.png';
    const secondUrl = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fsecond.png';

    let resolveFirst!: (blob: Blob) => void;
    const firstFetchPromise = new Promise<Blob>((resolve) => {
      resolveFirst = resolve;
    });
    hoisted.getPrinterFileThumbnail.mockImplementation((url: string) =>
      url === firstUrl ? firstFetchPromise : Promise.resolve(new Blob(['fake-png-bytes']))
    );

    const { result, rerender } = renderHook(
      ({ files }: { files: PrinterFileDto[] }) => usePrinterFileThumbnails(files),
      { initialProps: { files: [makeFile({ fileName: 'a.gcode', thumbnailUrl: firstUrl })] } }
    );

    await waitFor(() => {
      expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledWith(firstUrl, expect.any(AbortSignal));
    });
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(1);
    const firstCallSignal = hoisted.getPrinterFileThumbnail.mock.calls[0][1] as AbortSignal;

    // Grow the list before firstUrl resolves.
    rerender({
      files: [
        makeFile({ fileName: 'a.gcode', thumbnailUrl: firstUrl }),
        makeFile({ fileName: 'b.gcode', thumbnailUrl: secondUrl }),
      ],
    });

    await waitFor(() => {
      expect(result.current.objectUrls[secondUrl]).toBe('blob:mock-object-url');
    });

    // Only one fetch per url so far - firstUrl's original in-flight call must not have been
    // aborted and re-issued by the overlapping run that added secondUrl.
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(2);
    expect(firstCallSignal.aborted).toBe(false);

    resolveFirst(new Blob(['fake-png-bytes']));

    await waitFor(() => {
      expect(result.current.objectUrls[firstUrl]).toBe('blob:mock-object-url');
    });

    // Still exactly one call for firstUrl - it was never restarted.
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(2);
  });

  it('#2393: resolves the thumbnail correctly under React StrictMode mount replay instead of permanently failing it', async () => {
    // Regression coverage for the round-3 finding: StrictMode (development) synchronously
    // replays every effect on initial mount as setup -> cleanup -> setup, specifically to
    // prove setup can undo whatever cleanup did. A hook-lifetime-shared AbortController that
    // is aborted in the mount-tracking effect's cleanup but never recreated in its setup would
    // leave the very first thumbnail fetch (started during the first setup pass, before the
    // replay) permanently using an aborted signal - marking it failed forever instead of
    // resolving. The per-url controller + ownership-check design must instead let the replay's
    // second setup naturally reissue a fresh fetch, while the original (now-superseded) fetch's
    // eventual rejection is discarded rather than committed.
    //
    // The mock below is deliberately abort-aware (round-4 review finding: a plain
    // `mockResolvedValue` ignores the AbortSignal entirely and resolves the very first,
    // pre-replay call regardless of whether it was later aborted - which makes the test pass
    // even against the broken round-2/round-3 implementation, since an abort-blind mock never
    // lets a "permanently aborted, never retried" fetch actually fail). The first call's
    // promise only ever settles via its `AbortSignal` - proving it must actually be superseded
    // and abandoned, not merely resolved anyway - while every subsequent call resolves
    // normally, standing in for the replay's fresh, non-aborted fetch.
    let callCount = 0;
    hoisted.getPrinterFileThumbnail.mockImplementation(
      (_url: string, signal?: AbortSignal) =>
        new Promise<Blob>((resolve, reject) => {
          const isFirstCall = callCount === 0;
          callCount += 1;
          if (isFirstCall) {
            if (signal?.aborted) {
              reject(new DOMException('Aborted', 'AbortError'));
              return;
            }
            signal?.addEventListener('abort', () =>
              reject(new DOMException('Aborted', 'AbortError'))
            );
            return;
          }
          resolve(new Blob(['fake-png-bytes']));
        })
    );
    const url = '/api/printers/00000000-0000-0000-0000-000000000001/files/thumbnail?filename=thumbs%2Fstrict.png';
    const files = [makeFile({ thumbnailUrl: url })];

    const { result } = renderHook(() => usePrinterFileThumbnails(files), {
      wrapper: StrictMode,
    });

    await waitFor(() => {
      expect(result.current.objectUrls[url]).toBe('blob:mock-object-url');
    });
    expect(result.current.failed[url]).toBeUndefined();

    // Pin the mechanism, not just the outcome: the pre-replay fetch really was aborted and
    // superseded by a genuinely fresh, second request - not silently resolved anyway.
    expect(hoisted.getPrinterFileThumbnail).toHaveBeenCalledTimes(2);
    const firstCallSignal = hoisted.getPrinterFileThumbnail.mock.calls[0][1] as AbortSignal;
    expect(firstCallSignal.aborted).toBe(true);
  });
});
