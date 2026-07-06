import type { ReactNode } from 'react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { STLPreviewModal } from './STLPreviewModal';

vi.mock('@react-three/fiber', () => ({
  Canvas: ({ children }: { children: ReactNode }) => <div data-testid="stl-canvas">{children}</div>,
  useFrame: vi.fn(),
  useThree: () => ({
    camera: {
      fov: 50,
      position: { set: vi.fn(), x: 0, y: 0, z: 150 },
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

describe('STLPreviewModal Component', () => {
  beforeEach(() => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      blob: vi.fn().mockResolvedValue(new Blob([createMinimalBinaryStl()])),
      arrayBuffer: vi.fn().mockResolvedValue(createMinimalBinaryStl()),
    }));
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('does not render when isOpen is false', () => {
    render(<STLPreviewModal isOpen={false} file={null} onClose={vi.fn()} />);

    expect(screen.queryByText(/STL Model Preview/i)).not.toBeInTheDocument();
  });

  it('renders when isOpen is true with File', async () => {
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} />);

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText('model.stl')).toBeInTheDocument();
    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
  });

  it('renders URL input by fetching a valid STL and showing the viewer', async () => {
    render(
      <STLPreviewModal
        isOpen
        fileUrl="http://example.com/model.stl"
        fileName="model.stl"
        onClose={vi.fn()}
      />
    );

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText('model.stl')).toBeInTheDocument();
    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(screen.queryByText('Error Loading Model')).not.toBeInTheDocument();
  });

  it('calls onClose when close button is clicked', async () => {
    const onClose = vi.fn();
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={onClose} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    fireEvent.click(screen.getByLabelText('Close'));

    expect(onClose).toHaveBeenCalled();
  });

  it('displays model information for a valid STL file', async () => {
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} />);

    expect(await screen.findByText('Triangles')).toBeInTheDocument();
    expect(screen.getByText('Vertices')).toBeInTheDocument();
    expect(screen.getByText('1')).toBeInTheDocument();
    expect(screen.getByText('3')).toBeInTheDocument();
  });

  it('renders STLViewer success state', async () => {
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
  });

  it('calls onUseModel when "Use This Model" button is clicked', async () => {
    const onUseModel = vi.fn();
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} onUseModel={onUseModel} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    fireEvent.click(screen.getByText(/Use This Model/i));

    expect(onUseModel).toHaveBeenCalled();
  });

  it('hides "Use This Model" button when onUseModel is not provided', async () => {
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} onUseModel={undefined} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(screen.queryByText(/Use This Model/i)).not.toBeInTheDocument();
  });

  it('displays close button in footer', async () => {
    render(<STLPreviewModal isOpen file={createStlFile()} onClose={vi.fn()} />);

    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    expect(screen.getAllByText(/Close/i).length).toBeGreaterThan(0);
  });

  it('handles both File and URL inputs', async () => {
    const { unmount: unmountFile } = render(
      <STLPreviewModal isOpen file={createStlFile('model1.stl')} onClose={vi.fn()} />
    );

    expect(screen.getByText('model1.stl')).toBeInTheDocument();
    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    unmountFile();

    const { unmount: unmountUrl } = render(
      <STLPreviewModal
        isOpen
        fileUrl="http://example.com/model2.stl"
        fileName="model2.stl"
        onClose={vi.fn()}
      />
    );

    expect(screen.getByText('model2.stl')).toBeInTheDocument();
    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
    unmountUrl();
  });

  it('modal accepts props correctly', async () => {
    render(
      <STLPreviewModal
        isOpen
        file={createStlFile()}
        fileName="Custom Name"
        onClose={vi.fn()}
        onUseModel={vi.fn()}
      />
    );

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText(/Custom Name/i)).toBeInTheDocument();
    expect(await screen.findByTestId('stl-canvas')).toBeInTheDocument();
  });
});
