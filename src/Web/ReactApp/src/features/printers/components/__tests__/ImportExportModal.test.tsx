import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { MockInstance } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// This jsdom version's Blob/File implementation does not provide `.text()`
// (only the legacy FileReader-based read path), but `ImportExportModal`'s
// `countPrintersInFile` calls `file.slice(...).text()` like a spec-compliant
// browser would. Polyfilling it here (test environment only, via the
// FileReader jsdom *does* implement) lets these tests exercise that
// production code path unmodified rather than rewriting it to suit the test
// environment.
if (typeof Blob !== 'undefined' && typeof Blob.prototype.text !== 'function') {
  Blob.prototype.text = function (this: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result ?? ''));
      reader.onerror = () => reject(reader.error);
      reader.readAsText(this);
    });
  };
}

interface TestImportProgress {
  index: number;
  name: string;
  status: 'Pending' | 'Imported' | 'Skipped' | 'Failed';
  id?: string;
  reason?: string;
}

const hoisted = vi.hoisted(() => {
  const state: { progressCallback?: (progress: TestImportProgress) => void } = {};
  const unsubscribeImportProgress = vi.fn();
  return {
    state,
    unsubscribeImportProgress,
    getPrinters: vi.fn().mockResolvedValue([]),
    uploadPrinterImport: vi.fn().mockResolvedValue(undefined),
    streamExportFile: vi.fn().mockResolvedValue(undefined),
    isConnected: vi.fn(() => true),
    start: vi.fn().mockResolvedValue(undefined),
    onPrinterImportProgress: vi.fn((callback: (progress: TestImportProgress) => void) => {
      state.progressCallback = callback;
      return unsubscribeImportProgress;
    }),
  };
});

function emitProgress(progress: TestImportProgress) {
  // The real SignalR callback synchronously updates React state, so this
  // must be wrapped like any other externally-triggered state update.
  act(() => {
    hoisted.state.progressCallback?.(progress);
  });
}

vi.mock('@/services/api', () => ({
  apiClient: {
    getPrinters: hoisted.getPrinters,
    uploadPrinterImport: hoisted.uploadPrinterImport,
    streamExportFile: hoisted.streamExportFile,
  },
}));

vi.mock('@/services/printerHubService', () => ({
  printerHubService: {
    isConnected: hoisted.isConnected,
    start: hoisted.start,
    onPrinterImportProgress: hoisted.onPrinterImportProgress,
  },
}));

vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getHubUrl: (path: string) => path,
}));

vi.mock('sonner', () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  },
}));

import ImportExportModal from '../ImportExportModal';

function renderModal(overrides: { onClose?: () => void; onComplete?: () => void } = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = overrides.onClose ?? vi.fn();
  const onComplete = overrides.onComplete ?? vi.fn();
  const utils = render(
    <QueryClientProvider client={client}>
      <ImportExportModal isOpen onClose={onClose} onComplete={onComplete} />
    </QueryClientProvider>,
  );
  return { ...utils, client, onClose, onComplete };
}

/**
 * Drives a 2-printer JSON import through the hidden native file input
 * (`FileUpload` renders it with `className="hidden"` when `buttonText` is
 * set, so there is no visible label to query by — the input itself is a
 * real, queryable `<input type="file">` element).
 */
async function startTestImport() {
  const file = new File([JSON.stringify([{ name: 'A' }, { name: 'B' }])], 'printers.json', {
    type: 'application/json',
  });
  const input = document.querySelector('input[type="file"]') as HTMLInputElement;
  fireEvent.change(input, { target: { files: [file] } });
  // Import has started once the progress table (with its Cancel button)
  // replaces the file-picker screen.
  await screen.findByRole('button', { name: 'Cancel' });
}

/**
 * `window.setInterval` is also used by Testing Library's own `waitFor`
 * polling (and by jsdom's `requestAnimationFrame` shim) — the *component's*
 * 500ms completion-poll interval is identified by its distinct delay
 * argument, not by call order/count, so these helpers filter for it
 * specifically instead of asserting on the raw spy call count.
 */
function completionIntervalCalls(setIntervalSpy: MockInstance) {
  return setIntervalSpy.mock.calls.filter(([, delay]) => delay === 500);
}

function getCompletionIntervalId(setIntervalSpy: MockInstance): unknown {
  const calls = completionIntervalCalls(setIntervalSpy);
  expect(calls.length).toBeGreaterThanOrEqual(1);
  const index = setIntervalSpy.mock.calls.indexOf(calls[calls.length - 1]);
  return setIntervalSpy.mock.results[index]?.value;
}

function wasClearedWith(clearIntervalSpy: MockInstance, intervalId: unknown): boolean {
  return clearIntervalSpy.mock.calls.some(([id]) => id === intervalId);
}

describe('ImportExportModal resource cleanup (#1146 item 10 lazy-unmount leak)', () => {
  beforeEach(() => {
    hoisted.state.progressCallback = undefined;
    hoisted.getPrinters.mockClear();
    hoisted.getPrinters.mockResolvedValue([]);
    hoisted.uploadPrinterImport.mockClear();
    hoisted.uploadPrinterImport.mockResolvedValue(undefined);
    hoisted.streamExportFile.mockClear();
    hoisted.streamExportFile.mockResolvedValue(undefined);
    hoisted.isConnected.mockClear();
    hoisted.isConnected.mockReturnValue(true);
    hoisted.start.mockClear();
    hoisted.start.mockResolvedValue(undefined);
    hoisted.unsubscribeImportProgress.mockClear();
    hoisted.onPrinterImportProgress.mockClear();
  });

  it('subscribes to import progress and starts exactly one completion-poll interval per import', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    renderModal();

    await startTestImport();

    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(1);
    setIntervalSpy.mockRestore();
  });

  it('disposes the SignalR subscription and completion interval on "Close Anyway" during an in-progress import', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    const { onClose } = renderModal();

    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);

    fireEvent.click(screen.getByRole('button', { name: 'Close modal' }));
    await screen.findByText('Close During Import?');
    fireEvent.click(screen.getByRole('button', { name: 'Close Anyway' }));

    await waitFor(() => expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1));
    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true);
    expect(onClose).toHaveBeenCalledTimes(1);

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });

  it('disposes the SignalR subscription and completion interval on Cancel during an in-progress import', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    const { onClose, onComplete } = renderModal();

    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await screen.findByText('Cancel Import?');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel Import' }));

    await waitFor(() => expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1));
    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true);
    expect(onComplete).toHaveBeenCalledTimes(1);
    expect(onClose).toHaveBeenCalledTimes(1);

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });

  it('disposes the SignalR subscription and completion interval automatically once every item resolves (natural completion)', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    renderModal();

    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);

    emitProgress({ index: 0, name: 'A', status: 'Imported', id: 'p-a' });
    emitProgress({ index: 1, name: 'B', status: 'Imported', id: 'p-b' });

    // The real 500ms interval must actually tick once (in wall-clock time)
    // to detect completion and dispose itself.
    await waitFor(
      () => expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true),
      { timeout: 3000 },
    );
    expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1);
    // The Cancel button becomes "Close" once complete.
    expect(await screen.findByRole('button', { name: 'Close' })).toBeInTheDocument();

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  }, 10000);

  it('disposes the SignalR subscription and completion interval when the modal unmounts mid-import (route change)', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    const { unmount } = renderModal();

    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);

    unmount();

    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true);
    expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1);

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });

  it('does not leave a dangling interval after Close Anyway even if a background progress event still arrives', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    renderModal();

    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);

    fireEvent.click(screen.getByRole('button', { name: 'Close modal' }));
    await screen.findByText('Close During Import?');
    fireEvent.click(screen.getByRole('button', { name: 'Close Anyway' }));
    await waitFor(() => expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1));
    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true);

    // The real service would not deliver more events once unsubscribed, but
    // this proves the modal itself does not depend on that: emitting one
    // directly must not throw, resurrect state, or schedule another
    // completion-poll interval on the unmounted tree.
    expect(() => emitProgress({ index: 0, name: 'A', status: 'Imported' })).not.toThrow();
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(1);

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });
});