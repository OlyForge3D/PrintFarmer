import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { GCodeViewer } from '../GCodeViewer3D';
import type { IGcodePreviewService, DetailedParsedGCode } from '@/features/slicer/services';

// Mock react-three/fiber and drei — they require WebGL
vi.mock('@react-three/fiber', () => ({
  Canvas: ({ children }: { children: React.ReactNode }) => <div data-testid="r3f-canvas">{children}</div>,
}));

vi.mock('@react-three/drei', () => ({
  Line: () => <div data-testid="r3f-line" />,
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

const THREE_LAYER_GCODE = `; test fixture
G28
G1 Z0.2 F3000
G1 X10 Y10 E1 F1500
G1 X20 Y10 E2
G1 Z0.4
G1 X10 Y10 E3
G1 X20 Y20 E4
G1 Z0.6
G1 X5 Y5 E5
`;

function createMockService(result?: DetailedParsedGCode): IGcodePreviewService {
  const defaultResult: DetailedParsedGCode = {
    layerCount: 3,
    layers: [
      { index: 0, z: 0.2, points: [{ x: 10, y: 10, z: 0.2, e: 1, feedRate: 1500, type: 'extrude', tool: 0 }] },
      { index: 1, z: 0.4, points: [{ x: 20, y: 20, z: 0.4, e: 3, feedRate: 1500, type: 'extrude', tool: 0 }] },
      { index: 2, z: 0.6, points: [{ x: 5, y: 5, z: 0.6, e: 5, feedRate: 1500, type: 'extrude', tool: 0 }] },
    ],
    tools: [0],
  };

  return {
    parseGCode: vi.fn().mockResolvedValue({ layers: [], layerCount: 0 }),
    parseGCodeDetailed: vi.fn().mockResolvedValue(result ?? defaultResult),
    dispose: vi.fn(),
  };
}

describe('GCodeViewer3D', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    global.fetch = vi.fn().mockResolvedValue({
      ok: true,
      text: () => Promise.resolve(THREE_LAYER_GCODE),
    });
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

  it('shows error state on fetch failure', async () => {
    global.fetch = vi.fn().mockResolvedValue({
      ok: false,
      status: 404,
      text: () => Promise.resolve(''),
    });
    const service = createMockService();

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

  it('calls parseGCodeDetailed on the service (not direct parser)', async () => {
    const service = createMockService();

    render(<GCodeViewer gcodeUrl="/test.gcode" service={service} />);

    await waitFor(() => {
      expect(service.parseGCodeDetailed).toHaveBeenCalledWith(THREE_LAYER_GCODE);
    });
  });

  it('renders tool filter when multiple tools present', async () => {
    const multiToolResult: DetailedParsedGCode = {
      layerCount: 2,
      layers: [
        { index: 0, z: 0.2, points: [
          { x: 10, y: 10, z: 0.2, e: 1, feedRate: 3000, type: 'extrude', tool: 0 },
        ] },
        { index: 1, z: 0.4, points: [
          { x: 20, y: 20, z: 0.4, e: 2, feedRate: 3000, type: 'extrude', tool: 1 },
        ] },
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
