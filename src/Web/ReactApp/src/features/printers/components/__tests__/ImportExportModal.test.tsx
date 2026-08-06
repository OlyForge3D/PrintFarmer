import '@testing-library/jest-dom';
import React, { StrictMode } from 'react';
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

function renderModal(
  overrides: { onClose?: () => void; onComplete?: () => void; strictMode?: boolean } = {},
) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const onClose = overrides.onClose ?? vi.fn();
  const onComplete = overrides.onComplete ?? vi.fn();
  const tree = (
    <QueryClientProvider client={client}>
      <ImportExportModal isOpen onClose={onClose} onComplete={onComplete} />
    </QueryClientProvider>
  );
  // StrictMode (development) synchronously replays this component's mount
  // as setup -> cleanup -> setup before the tests below ever get to
  // interact with it — exercising that replay is the whole point of the
  // `strictMode` option, see the describe block guarding mountedRef restoration.
  const utils = render(overrides.strictMode ? <StrictMode>{tree}</StrictMode> : tree);
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

/** A promise plus its resolve/reject, for precisely sequencing races in tests. */
interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
  reject: (reason?: unknown) => void;
}

function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/**
 * Yields a full macrotask turn so every microtask queued by resolving/rejecting
 * a `Deferred` — including the chained `.then`s inside the component under
 * test — has already run by the time this resolves. Real timers are in
 * effect for this whole file (see the natural-completion test below), so a
 * 0ms `setTimeout` is enough to guarantee that.
 */
async function flushMicrotasks() {
  await new Promise<void>(resolve => setTimeout(resolve, 0));
}

/** Matches the `<strong>File:</strong> {fileName}` line by its exact text content. */
function fileInfoText(name: string) {
  return (_content: string, element: Element | null) => element?.textContent === `File: ${name}`;
}

// Shared by every describe block below so a test that overrides a mock's
// return value (e.g. to defer or reject it) never leaks into the next test.
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

describe('ImportExportModal resource cleanup (#1146 item 10 lazy-unmount leak)', () => {
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

describe('ImportExportModal startImport() mounted + operation-generation guard (#1146 re-review — Hicks)', () => {
  it('does not start the hub, subscribe, or update state if the component unmounts while file.text() is pending', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const textDeferred = createDeferred<string>();
    const textSpy = vi.spyOn(Blob.prototype, 'text').mockReturnValueOnce(textDeferred.promise);

    const { unmount } = renderModal();
    const file = new File([JSON.stringify([{ name: 'A' }, { name: 'B' }])], 'printers.json', {
      type: 'application/json',
    });
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    // countPrintersInFile() is still awaiting file.text() — nothing has run yet.
    expect(hoisted.start).not.toHaveBeenCalled();

    unmount();

    // Resolve file.text() *after* unmount: the startImport() continuation
    // must recognize the component is gone and stop before doing anything
    // else (no hub connect, no listener/interval, no setState).
    textDeferred.resolve(JSON.stringify([{ name: 'A' }, { name: 'B' }]));
    await act(async () => {
      await flushMicrotasks();
    });

    expect(hoisted.start).not.toHaveBeenCalled();
    expect(hoisted.onPrinterImportProgress).not.toHaveBeenCalled();
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(0);
    expect(hoisted.uploadPrinterImport).not.toHaveBeenCalled();

    setIntervalSpy.mockRestore();
    textSpy.mockRestore();
  });

  it('does not subscribe or start the completion interval if the component unmounts while printerHubService.start() is pending', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    hoisted.isConnected.mockReturnValue(false);
    const startDeferred = createDeferred<void>();
    hoisted.start.mockReturnValue(startDeferred.promise);

    const { unmount } = renderModal();
    const file = new File([JSON.stringify([{ name: 'A' }, { name: 'B' }])], 'printers.json', {
      type: 'application/json',
    });
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [file] } });

    // The "importing" state is already committed (it's set before the hub
    // connects) but startImport() itself is suspended awaiting the hub.
    await waitFor(() => expect(hoisted.start).toHaveBeenCalledTimes(1));
    expect(hoisted.onPrinterImportProgress).not.toHaveBeenCalled();

    unmount();

    // Resolve the hub connection *after* unmount.
    startDeferred.resolve();
    await act(async () => {
      await flushMicrotasks();
    });

    expect(hoisted.onPrinterImportProgress).not.toHaveBeenCalled();
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(0);
    expect(hoisted.uploadPrinterImport).not.toHaveBeenCalled();

    setIntervalSpy.mockRestore();
  });

  it('disposes the SignalR subscription and completion interval when the upload request is rejected after they were created', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    // A deferred (rather than an immediately-rejecting mock) lets the test
    // assert the listener/interval definitely exist *before* the rejection
    // is observed — react 18+'s automatic batching can otherwise collapse
    // the transient "importing" state and its immediate revert into a
    // single commit when a mock rejects on the very same tick it's awaited.
    const uploadDeferred = createDeferred<void>();
    hoisted.uploadPrinterImport.mockReturnValueOnce(uploadDeferred.promise);

    renderModal();
    await startTestImport();
    const intervalId = getCompletionIntervalId(setIntervalSpy);
    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);

    // Reject the upload now that the listener/interval definitely exist.
    uploadDeferred.reject(new Error('network exploded'));

    await waitFor(() => expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1));
    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(true);
    // Preserves the existing UX: the failure surfaces and the modal returns
    // to the file picker instead of leaving a dead "importing" screen up.
    expect(document.querySelector('input[type="file"]')).not.toBeNull();

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });

  it('a stale first startImport() continuation cannot attach resources or overwrite state once a second import has started', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const textDeferred = createDeferred<string>();
    // Only the *first* file.text() call (the doomed first import) hangs; the
    // second import's own file.text() call falls through to the real
    // (polyfilled) implementation and resolves normally.
    const textSpy = vi.spyOn(Blob.prototype, 'text').mockReturnValueOnce(textDeferred.promise);

    renderModal();

    const staleFile = new File(
      [JSON.stringify([{ name: 'Stale-1' }, { name: 'Stale-2' }, { name: 'Stale-3' }])],
      'stale.json',
      { type: 'application/json' },
    );
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, { target: { files: [staleFile] } });

    // The first import is still parsing its file — nothing has happened yet.
    expect(hoisted.uploadPrinterImport).not.toHaveBeenCalled();

    // A second import starts, and runs to completion, before the first's
    // prelude resolves.
    await startTestImport();

    expect(hoisted.uploadPrinterImport).toHaveBeenCalledTimes(1);
    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);
    expect(screen.getByText(fileInfoText('printers.json'))).toBeInTheDocument();

    // Now let the stale first import's file.text() resolve.
    textDeferred.resolve(JSON.stringify([{ name: 'Stale-1' }, { name: 'Stale-2' }, { name: 'Stale-3' }]));
    await act(async () => {
      await flushMicrotasks();
    });

    // The stale continuation must not have attached a second listener or
    // completion interval, re-uploaded, or overwritten the active (second)
    // import's file name / progress state.
    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(1);
    expect(hoisted.uploadPrinterImport).toHaveBeenCalledTimes(1);
    expect(screen.getByText(fileInfoText('printers.json'))).toBeInTheDocument();
    expect(screen.queryByText(fileInfoText('stale.json'))).not.toBeInTheDocument();

    setIntervalSpy.mockRestore();
    textSpy.mockRestore();
  });
});

describe('ImportExportModal StrictMode mount replay (final review — Hicks: setup must restore mountedRef, not just seed it once)', () => {
  it('completes a normal import startup under StrictMode: parses the file, connects the hub, registers the listener + completion interval, and uploads', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    // Force the "hub not yet connected" branch too, so this exercises every
    // isCurrentImport() checkpoint in startImport()'s prelude — the one
    // right after file parsing *and* the one right after the hub connects —
    // not just the first.
    hoisted.isConnected.mockReturnValue(false);
    renderModal({ strictMode: true });

    // StrictMode's synchronous setup -> cleanup -> setup mount replay has
    // already happened by the time render() returns. Before this fix, the
    // replay's cleanup left mountedRef.current stuck on `false` forever
    // (setup had nothing that restored it), so every isCurrentImport()
    // check below would fail and startImport() would silently bail out
    // before ever reaching printerHubService or apiClient — the progress
    // table would never replace the file picker and this await would
    // time out.
    await startTestImport();

    await waitFor(() => expect(hoisted.uploadPrinterImport).toHaveBeenCalledTimes(1));
    expect(hoisted.start).toHaveBeenCalledTimes(1);
    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(1);
    expect(screen.getByText(fileInfoText('printers.json'))).toBeInTheDocument();

    setIntervalSpy.mockRestore();
  });

  it('does not leave duplicate listeners or completion intervals from the StrictMode replay, and disposes the real ones exactly once on unmount', async () => {
    const setIntervalSpy = vi.spyOn(window, 'setInterval');
    const clearIntervalSpy = vi.spyOn(window, 'clearInterval');
    const { unmount } = renderModal({ strictMode: true });

    await startTestImport();

    // Exactly one *live* listener/interval must exist: the replay's setup
    // only restores mountedRef — it does not call startImport() or
    // register anything by itself — so it cannot have left a second,
    // orphaned registration behind alongside the one the file-select
    // actually created.
    expect(hoisted.onPrinterImportProgress).toHaveBeenCalledTimes(1);
    expect(completionIntervalCalls(setIntervalSpy)).toHaveLength(1);
    const intervalId = getCompletionIntervalId(setIntervalSpy);
    expect(wasClearedWith(clearIntervalSpy, intervalId)).toBe(false);

    unmount();

    // The real unmount disposes the one, still-live listener/interval
    // exactly once. The earlier StrictMode replay's own cleanup ran long
    // before the import started and had nothing registered yet to
    // dispose, so it cannot inflate this count.
    expect(hoisted.unsubscribeImportProgress).toHaveBeenCalledTimes(1);
    expect(clearIntervalSpy.mock.calls.filter(([id]) => id === intervalId)).toHaveLength(1);

    setIntervalSpy.mockRestore();
    clearIntervalSpy.mockRestore();
  });
});