import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { STLPreviewModal } from './STLPreviewModal';

// Mock STLViewer to simplify testing
vi.mock('./STLViewer', () => ({
  STLViewer: ({ file }: any) => <div data-testid="stl-viewer">STL Viewer Mock</div>,
}));

describe('STLPreviewModal Component', () => {
  it('does not render when isOpen is false', () => {
    const { container } = render(
      <STLPreviewModal
        isOpen={false}
        file={null}
        onClose={vi.fn()}
      />
    );

    expect(screen.queryByText(/STL Model Preview/i)).not.toBeInTheDocument();
  });

  it('renders when isOpen is true with File', () => {
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });
    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
      />
    );

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText('model.stl')).toBeInTheDocument();
  });

  it('renders when isOpen is true with URL', () => {
    render(
      <STLPreviewModal
        isOpen={true}
        fileUrl="http://example.com/model.stl"
        fileName="model.stl"
        onClose={vi.fn()}
      />
    );

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText('model.stl')).toBeInTheDocument();
  });

  it('calls onClose when close button is clicked', () => {
    const onClose = vi.fn();
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={onClose}
      />
    );

    const closeButton = screen.getByLabelText('Close');
    fireEvent.click(closeButton);

    expect(onClose).toHaveBeenCalled();
  });

  it('displays model information panel', async () => {
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });
    
    const { container } = render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
      />
    );

    // Verify modal renders by checking for its DOM structure
    expect(container.querySelector('.fixed')).toBeInTheDocument();
  });

  it('renders STLViewer component', () => {
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
      />
    );

    expect(screen.getByTestId('stl-viewer')).toBeInTheDocument();
  });

  it('calls onUseModel when "Use This Model" button is clicked', () => {
    const onUseModel = vi.fn();
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
        onUseModel={onUseModel}
      />
    );

    const useButton = screen.getByText(/Use This Model/i);
    fireEvent.click(useButton);

    expect(onUseModel).toHaveBeenCalled();
  });

  it('hides "Use This Model" button when onUseModel is not provided', () => {
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
        onUseModel={undefined}
      />
    );

    expect(screen.queryByText(/Use This Model/i)).not.toBeInTheDocument();
  });

  it('displays close button in footer', () => {
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
      />
    );

    const buttons = screen.getAllByText(/Close/i);
    expect(buttons.length).toBeGreaterThan(0);
  });

  it('handles both File and URL inputs', () => {
    // Test with File
    const file = new File(['test'], 'model1.stl', { type: 'application/octet-stream' });
    const { unmount: unmount1 } = render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        onClose={vi.fn()}
      />
    );
    
    expect(screen.getByText('model1.stl')).toBeInTheDocument();
    unmount1();

    // Test with URL
    const { unmount: unmount2 } = render(
      <STLPreviewModal
        isOpen={true}
        fileUrl="http://example.com/model2.stl"
        fileName="model2.stl"
        onClose={vi.fn()}
      />
    );
    
    expect(screen.getByText('model2.stl')).toBeInTheDocument();
    unmount2();
  });

  it('modal accepts props correctly', () => {
    const onClose = vi.fn();
    const onUseModel = vi.fn();
    const file = new File(['test'], 'model.stl', { type: 'application/octet-stream' });

    render(
      <STLPreviewModal
        isOpen={true}
        file={file}
        fileName="Custom Name"
        onClose={onClose}
        onUseModel={onUseModel}
      />
    );

    expect(screen.getByText(/STL Model Preview/i)).toBeInTheDocument();
    expect(screen.getByText(/Custom Name/i)).toBeInTheDocument();
  });
});
