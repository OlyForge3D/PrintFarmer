import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { GCodeViewer } from '../GCodeViewer3D';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import type { IGcodePreviewService, DetailedParsedGCode, DetailedLayer, BedBounds } from '@/features/slicer/services';
import type { GcodeBounds } from '../gcodeSceneMath';

// Captures every merged line stream the viewer mounts, so tests can verify
// the render-object count stays constant while vertex counts track the data.
const renderedStreams: Array<{ vertices: number; drawCount: number; opacity?: number }> = [];

// Mock react-three/fiber and drei — they require WebGL
vi.mock('@react-three/fiber', () => ({
  Canvas: ({ children }: { children: React.ReactNode }) => <div data-testid="r3f-canvas">{children}</div>,
}));

vi.mock('@react-three/drei', () => ({
  OrbitControls: () => null,
  Grid: () => null,
}));

const renderedBeds: Array<{ bed: BedBounds; showGrid: boolean }> = [];
const cameraFitBounds: Array<GcodeBounds | null> = [];

vi.mock('../GcodeSceneHelpers', () => ({
  GcodeBed: ({ bed, showGrid }: { bed: BedBounds; showGrid: boolean }) => {
    renderedBeds.push({ bed, showGrid });
    return <div data-testid="gcode-bed" />;
  },
  GcodeCameraFit: ({ bounds }: { bounds: GcodeBounds | null }) => {
    cameraFitBounds.push(bounds);
    return null;
  },
}));

vi.mock('../GcodeLineSegments', () => ({
  GcodeLineSegments: ({ stream, drawCount, opacity }: { stream: { positions: Float32Array }; drawCount: number; opacity?: number }) => {
    renderedStreams.push({ vertices: stream.positions.length / 3, drawCount, opacity });
    return <div data-testid="gcode-line-segments" />;
  },
}));

const THEME_ROOT = resolve(process.cwd(), 'src/design-system/themes');
const GRAPHIC_CONTRAST_MINIMUM = 3;

/**
 * Builds a `DetailedLayer` from plain per-point descriptors using real typed
 * arrays — matching the zero-copy Structure-of-Arrays shape `GCodePath`
 * actually consumes (#1788), instead of the retired `points: GCodePoint[]`
 * shape.
 */
function makeDetailedLayer(
  index: number,
  z: number,
  points: Array<{ x: number; y: number; z: number; e: number; feedRate: number; type: 'move' | 'extrude'; tool: number }>,
): DetailedLayer {
  const count = points.length;
  return {
    index,
    z,
    count,
    x: Float32Array.from(points.map(p => p.x)),
    y: Float32Array.from(points.map(p => p.y)),
    pz: Float32Array.from(points.map(p => p.z)),
    e: Float32Array.from(points.map(p => p.e)),
    feedRate: Float32Array.from(points.map(p => p.feedRate)),
    type: Uint8Array.from(points.map(p => (p.type === 'extrude' ? 1 : 0))),
    tool: Int32Array.from(points.map(p => p.tool)),
  };
}

function createMockService(result?: DetailedParsedGCode): IGcodePreviewService {
  const defaultResult: DetailedParsedGCode = {
    layerCount: 3,
    layers: [
      makeDetailedLayer(0, 0.2, [{ x: 10, y: 10, z: 0.2, e: 1, feedRate: 1500, type: 'extrude', tool: 0 }]),
      makeDetailedLayer(1, 0.4, [{ x: 20, y: 20, z: 0.4, e: 3, feedRate: 1500, type: 'extrude', tool: 0 }]),
      makeDetailedLayer(2, 0.6, [{ x: 5, y: 5, z: 0.6, e: 5, feedRate: 1500, type: 'extrude', tool: 0 }]),
    ],
    tools: [0],
  };

  return {
    parseGCode: vi.fn().mockResolvedValue({ layers: [], layerCount: 0 }),
    parseGCodeDetailed: vi.fn().mockResolvedValue(result ?? defaultResult),
    dispose: vi.fn(),
  };
}

function readThemeToken(theme: string, token: string): string {
  const source = readFileSync(resolve(THEME_ROOT, `${theme}.css`), 'utf8');
  const match = source.match(new RegExp(`--pf-${token}:\\s*(#[0-9a-f]{3,8})`, 'i'));
  if (!match) throw new Error(`${theme} is missing --pf-${token}`);
  return match[1];
}

function relativeLuminance(hex: string): number {
  const value = hex.slice(1);
  const expanded = value.length === 3
    ? value.split('').map(character => `${character}${character}`).join('')
    : value;
  const channel = (offset: number): number => {
    const normalized = Number.parseInt(expanded.slice(offset, offset + 2), 16) / 255;
    return normalized <= 0.04045
      ? normalized / 12.92
      : ((normalized + 0.055) / 1.055) ** 2.4;
  };

  return 0.2126 * channel(0) + 0.7152 * channel(2) + 0.0722 * channel(4);
}

function contrastRatio(first: string, second: string): number {
  const firstLuminance = relativeLuminance(first);
  const secondLuminance = relativeLuminance(second);
  const lighter = Math.max(firstLuminance, secondLuminance);
  const darker = Math.min(firstLuminance, secondLuminance);
  return (lighter + 0.05) / (darker + 0.05);
}

describe('GCodeViewer3D', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    renderedStreams.length = 0;
    renderedBeds.length = 0;
    cameraFitBounds.length = 0;
  });

  it('draws the printer bed declared in the G-code under the model', async () => {
    const declaredBed: BedBounds = { minX: 0, minY: 0, maxX: 256, maxY: 256 };
    const service = createMockService({
      layerCount: 1,
      layers: [makeDetailedLayer(0, 0.2, [{ x: 10, y: 10, z: 0.2, e: 1, feedRate: 1500, type: 'extrude', tool: 0 }])],
      tools: [0],
      bed: declaredBed,
    });

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    await waitFor(() => expect(screen.getByText(/Layer 1 \/ 1/)).toBeInTheDocument());

    expect(renderedBeds.at(-1)).toEqual({ bed: declaredBed, showGrid: true });
  });

  it('falls back to a bed covering the toolpath when the file declares none', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    await waitFor(() => expect(screen.getByText(/Layer 3 \/ 3/)).toBeInTheDocument());

    // Default fixture spans x/y 5..20 -> origin-inclusive, 10 mm margin, 10 mm snapped.
    expect(renderedBeds.at(-1)?.bed).toEqual({ minX: -10, minY: -10, maxX: 30, maxY: 30 });
  });

  it('fits the camera to extrusion bounds, ignoring far-away travel moves', async () => {
    const service = createMockService({
      layerCount: 2,
      layers: [
        makeDetailedLayer(0, 0.2, [
          { x: 50, y: 60, z: 0.2, e: 1, feedRate: 1500, type: 'extrude', tool: 0 },
          { x: 70, y: 80, z: 0.2, e: 2, feedRate: 1500, type: 'extrude', tool: 0 },
        ]),
        makeDetailedLayer(1, 10, [
          { x: 60, y: 70, z: 10, e: 3, feedRate: 1500, type: 'extrude', tool: 0 },
          { x: 0, y: 250, z: 50, e: 3, feedRate: 9000, type: 'move', tool: 0 },
        ]),
      ],
      tools: [0],
    });

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    await waitFor(() => expect(screen.getByText(/Layer 2 \/ 2/)).toBeInTheDocument());

    expect(cameraFitBounds.at(-1)).toEqual({ minX: 50, minY: 60, minZ: Math.fround(0.2), maxX: 70, maxY: 80, maxZ: 10 });
  });

  it('keeps the same camera-fit bounds object while scrubbing layers', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    await waitFor(() => expect(screen.getByText(/Layer 3 \/ 3/)).toBeInTheDocument());
    const fitted = cameraFitBounds.at(-1);

    await act(async () => {
      fireEvent.change(screen.getByLabelText('Layer slider'), { target: { value: '0' } });
    });

    expect(cameraFitBounds.at(-1)).toBe(fitted);
  });

  it('shows loading spinner while parsing', () => {
    const service = createMockService();
    // Make parseGCodeDetailed never resolve during this test
    (service.parseGCodeDetailed as ReturnType<typeof vi.fn>).mockReturnValue(new Promise(() => {}));

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('renders layer info after successful parse', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByText(/Layer 3 \/ 3/)).toBeInTheDocument();
    });
  });

  it('passes authorization headers to the worker-backed preview service', async () => {
    const service = createMockService();
    const requestHeaders = { Authorization: 'Bearer preview-token' };

    render(
      <GCodeViewer
        gcodeUrl="/api/artifacts/artifact-1"
        requestHeaders={requestHeaders}
        service={service}
      />,
    );

    await waitFor(() => {
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith(
        '/api/artifacts/artifact-1',
        requestHeaders,
        expect.any(AbortSignal),
      );
    });
  });

  it('keeps the real settings control visible, named, and keyboard operable on hover', async () => {
    const user = userEvent.setup();
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    const settingsButton = await screen.findByRole('button', { name: 'Settings' });
    expect(settingsButton).toHaveAttribute('type', 'button');
    expect(settingsButton).toHaveAttribute('title', 'Settings');
    expect(settingsButton).toHaveAttribute('data-pf-variant', 'subtle');
    expect(settingsButton).toHaveClass(
      'enabled:hover:text-pf-text-primary',
      'border-none',
      'shadow-none',
      'focus:ring-0',
    );
    expect(settingsButton).not.toHaveClass('hover:text-white', 'text-pf-text-secondary');
    expect(settingsButton.closest('.bg-linear-to-r')).toHaveClass('from-pf-bg-0', 'to-pf-bg-0');

    settingsButton.focus();
    await user.keyboard('{Enter}');
    expect(screen.getByRole('heading', { name: 'Rendering' })).toBeInTheDocument();
  });

  it.each(SELECTABLE_THEMES)(
    '%s keeps the settings hover foreground visible on the subtle hover surface',
    theme => {
      const hoverForeground = readThemeToken(theme, 'text-primary');
      const restingForeground = readThemeToken(theme, 'text-secondary');
      const hoverBackground = readThemeToken(theme, 'bg-1');
      const restingSurface = readThemeToken(theme, 'bg-0');

      expect(hoverForeground).not.toBe(restingForeground);
      expect(hoverBackground).not.toBe(restingSurface);
      expect(contrastRatio(hoverForeground, hoverBackground)).toBeGreaterThanOrEqual(
        GRAPHIC_CONTRAST_MINIMUM,
      );
    },
  );

  it('shows error state on fetch failure', async () => {
    const service = createMockService();
    (service.parseGCodeDetailed as ReturnType<typeof vi.fn>).mockRejectedValue(
      new Error('404 Not Found'),
    );

    render(<GCodeViewer gcodeUrl="/missing.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.getByText(/Failed to load G-code/)).toBeInTheDocument();
    });
  });

  it('shows error state on parse failure', async () => {
    const service = createMockService();
    (service.parseGCodeDetailed as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('Parse error'));

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.getByText('Parse error')).toBeInTheDocument();
    });
  });

  it('calls parseGCodeDetailed on the service with the gcode URL (fetch happens inside the service/worker, not the component)', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith(
        '/test.gcode',
        {},
        expect.any(AbortSignal),
      );
    });
  });

  it('cancels the superseded parse via AbortSignal when gcodeUrl changes before it resolves (#1808 concurrency finding)', async () => {
    const service = createMockService();
    let resolveFirst: ((value: DetailedParsedGCode) => void) | undefined;
    (service.parseGCodeDetailed as ReturnType<typeof vi.fn>).mockImplementationOnce(
      () => new Promise<DetailedParsedGCode>((resolve) => { resolveFirst = resolve; }),
    );

    const { rerender } = render(<GCodeViewer gcodeUrl="/stale.gcode" service={service} />);

    await waitFor(() => {
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith(
        '/stale.gcode',
        {},
        expect.any(AbortSignal),
      );
    });
    const staleSignal = (service.parseGCodeDetailed as ReturnType<typeof vi.fn>).mock.calls[0][2] as AbortSignal;
    expect(staleSignal.aborted).toBe(false);

    // Switching gcodeUrl before the stale parse resolves must abort the
    // signal passed for it — this is what lets the service terminate the
    // worker running it, rather than merely ignoring its eventual result.
    rerender(<GCodeViewer gcodeUrl="/new.gcode" service={service} />);

    expect(staleSignal.aborted).toBe(true);
    await waitFor(() => {
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith(
        '/new.gcode',
        {},
        expect.any(AbortSignal),
      );
    });

    // Resolving the superseded (now-aborted) request afterwards must not
    // clobber state derived from the newer request.
    await act(async () => {
      resolveFirst?.({ layers: [], layerCount: 0, tools: [] });
      await Promise.resolve();
    });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('renders tool filter when multiple tools present', async () => {
    const multiToolResult: DetailedParsedGCode = {
      layerCount: 2,
      layers: [
        makeDetailedLayer(0, 0.2, [{ x: 10, y: 10, z: 0.2, e: 1, feedRate: 3000, type: 'extrude', tool: 0 }]),
        makeDetailedLayer(1, 0.4, [{ x: 20, y: 20, z: 0.4, e: 2, feedRate: 3000, type: 'extrude', tool: 1 }]),
      ],
      tools: [0, 1],
    };
    const service = createMockService(multiToolResult);

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByText('Filament / Tool Filter')).toBeInTheDocument();
      expect(screen.getByLabelText('Toggle tool 0')).toBeInTheDocument();
      expect(screen.getByLabelText('Toggle tool 1')).toBeInTheDocument();
    });
  });

  it('renders one merged extrusion stream regardless of segment count, and scrubs by draw range', async () => {
    const user = userEvent.setup();
    // 2 layers, 5000 extrusion points each: the old renderer would mount one
    // <Line> per type-run; the merged renderer must mount a constant count.
    const bigLayer = (index: number, z: number) => makeDetailedLayer(
      index,
      z,
      Array.from({ length: 5000 }, (_, i) => ({
        x: i % 100, y: Math.floor(i / 100), z, e: i, feedRate: 1500,
        type: (i % 2 === 0 ? 'extrude' : 'move') as 'extrude' | 'move', tool: 0,
      })),
    );
    const service = createMockService({ layerCount: 2, layers: [bigLayer(0, 0.2), bigLayer(1, 0.4)], tools: [0] });

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);
    await waitFor(() => expect(screen.getByText(/Layer 2 \/ 2/)).toBeInTheDocument());

    // Travel hidden by default -> exactly one stream mounted.
    const last = renderedStreams.slice(-1);
    expect(last).toHaveLength(1);
    // 10000 points -> 9999 segments (the first point has no predecessor);
    // segments ending on even indices are extrusion: 4999 segments x 2 vertices.
    expect(last[0].vertices).toBe(9998);
    expect(last[0].drawCount).toBe(9998);

    renderedStreams.length = 0;
    await act(async () => {
      fireEvent.change(screen.getByLabelText('Layer slider'), { target: { value: '0' } });
    });
    const scrubbed = renderedStreams[renderedStreams.length - 1];
    expect(scrubbed.vertices).toBe(9998);
    expect(scrubbed.drawCount).toBe(4998);

    renderedStreams.length = 0;
    await user.click(screen.getByLabelText('Travel Moves'));
    const withTravel = renderedStreams.slice(-2);
    expect(withTravel).toHaveLength(2);
    expect(withTravel[1].opacity).toBeLessThan(1);
    expect(withTravel[1].vertices).toBe(10000);
  });

  it('does not show tool filter for single-tool gcode', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByText(/Layer 3 \/ 3/)).toBeInTheDocument();
    });
    expect(screen.queryByText('Filament / Tool Filter')).not.toBeInTheDocument();
  });

  it('disposes the service on unmount', async () => {
    const service = createMockService();

    const { unmount } = render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByText(/Layer 3 \/ 3/)).toBeInTheDocument();
    });

    unmount();
    expect(service.dispose).toHaveBeenCalled();
  });
});
