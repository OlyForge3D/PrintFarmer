/**
 * Web Worker entry point for detailed G-code parsing (#1788).
 *
 * Design constraints (see #1766 spike + #1788 issue):
 *  - The worker fetches the raw G-code text ITSELF from a URL passed in the
 *    (tiny) request message. The (potentially tens-of-MB) text never
 *    round-trips across the worker boundary as a structured-clone payload.
 *  - The result is posted back as transferable typed arrays (Structure-of-
 *    Arrays), not per-point objects, so the transfer is zero-copy.
 *
 * This file is loaded via Vite's `new Worker(new URL(...), { type: 'module' })`
 * pattern from `gcodePreviewService.ts` and is never imported directly by
 * application code or tests — `gcodeParserCore.ts` holds the actual parsing
 * logic so it can be unit tested without spinning up a real Worker.
 */

import { parseDetailedLayersCore, detailedParseBuffersTransferList } from './gcodeParserCore';

export interface GCodeWorkerRequest {
  requestId: number;
  gcodeUrl: string;
}

export interface GCodeWorkerSuccessResponse {
  requestId: number;
  ok: true;
  buffers: ReturnType<typeof parseDetailedLayersCore>;
}

export interface GCodeWorkerErrorResponse {
  requestId: number;
  ok: false;
  error: string;
}

export type GCodeWorkerResponse = GCodeWorkerSuccessResponse | GCodeWorkerErrorResponse;

// `self` is typed via the DOM lib here (no `webworker` lib reference — it
// conflicts with DOM's `Window` types used elsewhere in this project's
// tsconfig). Cast narrowly where the Worker-only two-arg `postMessage`
// overload (message, transferList) is needed.
self.onmessage = async (event: MessageEvent<GCodeWorkerRequest>) => {
  const { requestId, gcodeUrl } = event.data;

  try {
    const res = await fetch(gcodeUrl);
    if (!res.ok) throw new Error(`Failed to load G-code: ${res.status}`);
    const gcodeText = await res.text();

    const buffers = parseDetailedLayersCore(gcodeText);
    const response: GCodeWorkerSuccessResponse = { requestId, ok: true, buffers };
    (self.postMessage as (message: unknown, transfer: Transferable[]) => void)(
      response,
      detailedParseBuffersTransferList(buffers),
    );
  } catch (err) {
    const response: GCodeWorkerErrorResponse = {
      requestId,
      ok: false,
      error: err instanceof Error ? err.message : String(err),
    };
    self.postMessage(response);
  }
};
