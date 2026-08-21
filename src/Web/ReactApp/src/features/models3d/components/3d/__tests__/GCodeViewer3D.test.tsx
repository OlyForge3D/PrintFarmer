import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import userEvent from '@testing-library/user-event';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { GCodeViewer } from '../GCodeViewer3D';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import type { IGcodePreviewService, DetailedParsedGCode, DetailedLayer } from '@/features/slicer/services';

// Captures the `points` prop passed to every rendered <Line>, so tests can
// verify the typed-array-driven rendering path (GCodePath) actually builds
// segments from layer.x/y/pz/feedRate/type rather than a no-op on stale mocks.
const renderedLinePointCounts: number[] = [];

// Mock react-three/fiber and drei — they require WebGL
vi.mock('@react-three/fiber', () => ({
  Canvas: ({ children }: { children: React.ReactNode }) => <div data-testid="r3f-canvas">{children}</div>,
}));

vi.mock('@react-three/drei', () => ({
  Line: ({ points }: { points: unknown[] }) => {
    renderedLinePointCounts.push(points.length);
    return <div data-testid="r3f-line" />;
  },
  OrbitControls: () => null,
  Grid: () => null,
}));

vi.mock('three', () => ({
  Color: class {
    setHSL() { return this; }
    set() { return this; }
    lerpColors() { return this; }
  },
  Vector3: class {
    constructor(public x = 0, public y = 0, public z = 0) {}
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
    renderedLinePointCounts.length = 0;
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
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith('/test.gcode');
    });
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

  it('renders Line segments built from the typed-array layer data (#1788)', async () => {
    // Layer 0: a Z-lift move (type 0) followed by three extrude points
    // (type 1) at distinct coordinates — enough points per segment (>1) for
    // GCodePath to actually emit <Line> elements, proving the typed-array
    // read path (layer.x/y/pz/feedRate/type) drives real rendering rather
    // than a no-op against stale `points` mocks.
    const layerWithSegments = makeDetailedLayer(0, 0.2, [
      { x: 0, y: 0, z: 0.2, e: 0, feedRate: 3000, type: 'move', tool: 0 },
      { x: 1, y: 0, z: 0.2, e: 0, feedRate: 3000, type: 'move', tool: 0 },
      { x: 10, y: 10, z: 0.2, e: 1, feedRate: 1500, type: 'extrude', tool: 0 },
      { x: 20, y: 10, z: 0.2, e: 2, feedRate: 1500, type: 'extrude', tool: 0 },
      { x: 20, y: 20, z: 0.2, e: 3, feedRate: 1500, type: 'extrude', tool: 0 },
    ]);
    const service = createMockService({
      layerCount: 1,
      layers: [layerWithSegments],
      tools: [0],
    });

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(screen.getByText(/Layer 1 \/ 1/)).toBeInTheDocument();
    });

    // Two segments: a 2-point move segment (indices 0-1) and a 3-point
    // extrude segment (indices 2-4), split exactly at the type transition.
    expect(renderedLinePointCounts.sort()).toEqual([2, 3]);
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
