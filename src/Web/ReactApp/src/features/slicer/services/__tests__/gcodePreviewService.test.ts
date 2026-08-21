import { describe, it, expect, afterEach, beforeEach, vi } from 'vitest';
import { createGcodePreviewService } from '../gcodePreviewService';
import { parseDetailedLayersCore, detailedParseBuffersTransferList } from '../gcodeParserCore';

const THREE_LAYER_GCODE = `; generated test fixture
G28 ; home
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
G1 X20 Y10 E2
G1 X20 Y20 E3
G1 Z0.4
G1 X10 Y10 E4
G1 X20 Y10 E5
G1 X20 Y20 E6
G1 Z0.6
G1 X10 Y10 E7
G1 X20 Y10 E8
G1 X20 Y20 E9
`;

const MULTI_TOOL_GCODE = `; multi-tool fixture
G28
T0
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
T1
G1 X20 Y20 E2
`;

describe('GcodePreviewService', () => {
  const service = createGcodePreviewService();

  afterEach(() => {
    service.dispose();
  });

  it('parses a 3-layer fixture and returns correct layer count', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    expect(result.layerCount).toBe(3);
    expect(result.layers).toHaveLength(3);
  });

  it('returns layer metadata for each layer', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    for (const layer of result.layers) {
      expect(layer).toHaveProperty('index');
      expect(layer).toHaveProperty('commandCount');
      expect(layer).toHaveProperty('z');
      expect(layer.commandCount).toBeGreaterThan(0);
    }
  });

  it('returns increasing Z heights across layers', async () => {
    const result = await service.parseGCode(THREE_LAYER_GCODE);

    for (let i = 1; i < result.layers.length; i++) {
      expect(result.layers[i].z).toBeGreaterThan(result.layers[i - 1].z);
    }
  });

  it('returns a Promise (async-ready for v2 worker swap)', async () => {
    const resultPromise = service.parseGCode(THREE_LAYER_GCODE);
    expect(resultPromise).toBeInstanceOf(Promise);
  });
});

describe('GcodePreviewService.parseGCodeDetailed (no Worker — main-thread fallback, e.g. jsdom)', () => {
  let service: ReturnType<typeof createGcodePreviewService>;

  beforeEach(() => {
    service = createGcodePreviewService();
    global.fetch = vi.fn().mockResolvedValue({
      ok: true,
      text: () => Promise.resolve(THREE_LAYER_GCODE),
    });
  });

  afterEach(() => {
    service.dispose();
    vi.restoreAllMocks();
  });

  it('fetches the given URL itself rather than accepting raw text (#1788)', async () => {
    await service.parseGCodeDetailed('/models/print.gcode');
    expect(global.fetch).toHaveBeenCalledWith('/models/print.gcode', expect.objectContaining({
      signal: expect.any(AbortSignal),
    }));
  });

  it('returns a detailed parse matching the pure parsing core', async () => {
    const result = await service.parseGCodeDetailed('/print.gcode');
    const expectedBuffers = parseDetailedLayersCore(THREE_LAYER_GCODE);

    expect(result.layerCount).toBe(expectedBuffers.layerCount);
    expect(result.tools).toEqual(Array.from(expectedBuffers.tools));
    // Layer 0: the Z-lift move (Z0.2, no E) plus 3 extrusion moves.
    expect(result.layers[0].count).toBe(4);

    const layer0 = result.layers[0];
    expect(layer0.type[0]).toBe(0); // move
    expect(layer0.tool[0]).toBe(0);
    expect(layer0.pz[0]).toBeCloseTo(0.2, 4);
    expect(layer0.feedRate[0]).toBeCloseTo(3000, 4);

    expect(layer0.type[1]).toBe(1); // extrude
    expect(layer0.x[1]).toBeCloseTo(10, 4);
    expect(layer0.y[1]).toBeCloseTo(10, 4);
    expect(layer0.e[1]).toBeCloseTo(1, 4);
  });

  it('groups points by tool across a tool change', async () => {
    (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: true,
      text: () => Promise.resolve(MULTI_TOOL_GCODE),
    });

    const result = await service.parseGCodeDetailed('/multi-tool.gcode');

    expect(result.tools).toEqual([0, 1]);
    const hasTool1 = result.layers.some(l => Array.from(l.tool).includes(1));
    expect(hasTool1).toBe(true);
  });

  it('rejects with a descriptive error when the fetch response is not ok', async () => {
    (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
      ok: false,
      status: 404,
      text: () => Promise.resolve(''),
    });

    await expect(service.parseGCodeDetailed('/missing.gcode')).rejects.toThrow(/404/);
  });

  it('aborts an in-flight fallback fetch when dispose() is called before it resolves', async () => {
    let capturedSignal: AbortSignal | undefined;
    global.fetch = vi.fn((_url: string, init?: RequestInit) => {
      capturedSignal = init?.signal ?? undefined;
      return new Promise<never>((_resolve, reject) => {
        capturedSignal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      });
    });

    const parsePromise = service.parseGCodeDetailed('/slow.gcode');
    service.dispose();

    await expect(parsePromise).rejects.toThrow();
    expect(capturedSignal?.aborted).toBe(true);
  });
});

describe('GcodePreviewService.parseGCodeDetailed (Worker path)', () => {
  interface FakeWorkerMessage {
    requestId: number;
    gcodeUrl: string;
  }

  class FakeWorker {
    static instances: FakeWorker[] = [];
    onmessage: ((event: MessageEvent) => void) | null = null;
    onerror: ((event: ErrorEvent) => void) | null = null;
    terminated = false;
    postMessage = vi.fn((message: FakeWorkerMessage) => {
      const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);
      queueMicrotask(() => {
        this.onmessage?.({ data: { requestId: message.requestId, ok: true, buffers } } as MessageEvent);
      });
    });
    terminate = vi.fn(() => { this.terminated = true; });

    constructor() {
      FakeWorker.instances.push(this);
    }
  }

  let originalWorker: typeof Worker | undefined;

  beforeEach(() => {
    originalWorker = globalThis.Worker;
    FakeWorker.instances = [];
    (globalThis as unknown as { Worker: unknown }).Worker = FakeWorker;
  });

  afterEach(() => {
    (globalThis as unknown as { Worker: unknown }).Worker = originalWorker;
    vi.restoreAllMocks();
  });

  it('routes parseGCodeDetailed through the Worker and reconstructs the point-object shape', async () => {
    const service = createGcodePreviewService();

    const result = await service.parseGCodeDetailed('/print.gcode');

    expect(FakeWorker.instances).toHaveLength(1);
    expect(FakeWorker.instances[0].postMessage).toHaveBeenCalledWith(
      expect.objectContaining({ gcodeUrl: '/print.gcode' }),
    );
    expect(result.layerCount).toBe(3);
    // Layer 0 begins with the Z-lift move (no E), then extrusion moves.
    expect(result.layers[0].type[0]).toBe(0); // move
    expect(result.layers[0].type[1]).toBe(1); // extrude

    service.dispose();
  });

  it('terminates the Worker on dispose', async () => {
    const service = createGcodePreviewService();
    await service.parseGCodeDetailed('/print.gcode');

    service.dispose();

    expect(FakeWorker.instances[0].terminated).toBe(true);
  });

  it('rejects pending requests when the Worker reports an error', async () => {
    class FailingFakeWorker extends FakeWorker {
      postMessage = vi.fn(() => {
        queueMicrotask(() => {
          this.onerror?.({ message: 'boom' } as ErrorEvent);
        });
      });
    }
    (globalThis as unknown as { Worker: unknown }).Worker = FailingFakeWorker;

    const service = createGcodePreviewService();
    await expect(service.parseGCodeDetailed('/print.gcode')).rejects.toThrow('boom');
    service.dispose();
  });
});

describe('detailedParseBuffersTransferList', () => {
  it('lists every typed array buffer for zero-copy transfer', () => {
    const buffers = parseDetailedLayersCore(THREE_LAYER_GCODE);
    const transferList = detailedParseBuffersTransferList(buffers);

    expect(transferList).toEqual([
      buffers.x.buffer,
      buffers.y.buffer,
      buffers.z.buffer,
      buffers.e.buffer,
      buffers.feedRate.buffer,
      buffers.tool.buffer,
      buffers.type.buffer,
      buffers.layerStart.buffer,
      buffers.layerZ.buffer,
      buffers.tools.buffer,
    ]);
  });
});
