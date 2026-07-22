import type { ReactNode } from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { STLViewer } from './STLViewer';

vi.mock('@react-three/fiber', () => ({
  Canvas: ({ children }: { children: ReactNode }) => <div data-testid="stl-canvas">{children}</div>,
  useFrame: vi.fn(),
  useThree: () => ({
    camera: {
      fov: 50,
      position: { set: vi.fn(), x: 0, y: 0, z: 100 },
      lookAt: vi.fn(),
      updateProjectionMatrix: vi.fn(),
    },
  }),
}));

vi.mock('@react-three/drei', () => ({
  Grid: () => <div data-testid="stl-grid" />,
  OrbitControls: () => <div data-testid="orbit-controls" />,
}));

vi.mock('./ViewCube', () => ({
  ViewCube: () => <div data-testid="view-cube" />,
}));

function createMinimalBinaryStl(): ArrayBuffer {
  const buffer = new ArrayBuffer(134);
  const view = new DataView(buffer);
  view.setUint32(80, 1, true);

  let offset = 84;
  view.setFloat32(offset, 0, true);
  view.setFloat32(offset + 4, 0, true);
  view.setFloat32(offset + 8, 1, true);
  offset += 12;

  const vertices = [
    [0, 0, 0],
    [1, 0, 0],
    [0, 1, 0],
  ];

  for (const vertex of vertices) {
    view.setFloat32(offset, vertex[0], true);
    view.setFloat32(offset + 4, vertex[1], true);
    view.setFloat32(offset + 8, vertex[2], true);
    offset += 12;
  }

  view.setUint16(offset, 0, true);
  return buffer;
}

function createStlFile(name = 'model.stl') {
  const file = new File(['valid-binary-stl'], name, { type: 'application/octet-stream' });
  Object.defineProperty(file, 'arrayBuffer', {
    value: vi.fn().mockResolvedValue(createMinimalBinaryStl()),
  });
  return file;
}

describe('STLViewer Component', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      arrayBuffer: vi.fn().mockResolvedValue(createMinimalBinaryStl()),
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders no-model state when no file provided', async () => {
    render(<STLViewer />);

    expect(await screen.findByText('No model loaded')).toBeInTheDocument();
  });

  it('accepts File prop and renders the STL canvas', async () => {
    render(<STLViewer file={createStlFile()} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(screen.queryByText('Error Loading Model')).not.toBeInTheDocument();
  });

  it('accepts ArrayBuffer prop and renders the STL canvas', async () => {
    render(<STLViewer file={createMinimalBinaryStl()} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(screen.queryByText('Error Loading Model')).not.toBeInTheDocument();
  });

  it('accepts URL string prop and renders the fetched STL canvas', async () => {
    render(<STLViewer file="http://example.com/model.stl" />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(fetch).toHaveBeenCalledWith('http://example.com/model.stl');
  });

  it('accepts autoRotate prop while rendering the model', async () => {
    render(<STLViewer file={createStlFile()} autoRotate={true} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
  });

  it('accepts custom camera position while rendering the model', async () => {
    const customPosition: [number, number, number] = [100, 200, 300];

    render(<STLViewer file={createStlFile()} cameraPosition={customPosition} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
  });

  it('calls onMeshLoaded callback after a valid STL File loads', async () => {
    const callback = vi.fn();

    render(<STLViewer file={createStlFile()} onMeshLoaded={callback} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    await waitFor(() => expect(callback).toHaveBeenCalledTimes(1));
  });

  it('renders one canvas per loaded STLViewer instance', async () => {
    render(
      <>
        <STLViewer file={createStlFile('first.stl')} />
        <STLViewer file={createStlFile('second.stl')} />
      </>
    );

    await waitFor(() => {
      expect(screen.getAllByTestId('stl-canvas')).toHaveLength(2);
    });
    expect(screen.queryByText('Error Loading Model')).not.toBeInTheDocument();
  });
});
