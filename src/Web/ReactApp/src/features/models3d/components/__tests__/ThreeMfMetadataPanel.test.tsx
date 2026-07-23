import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ThreeMfMetadataPanel } from '@/features/models3d/components/ThreeMfMetadataPanel';
import type { ThreeMfMetadata } from '@/types/models';

const mockMetadata: ThreeMfMetadata = {
  title: 'Test Model',
  designer: 'Jane Doe',
  description: 'A test model',
  application: 'OrcaSlicer',
  creationDate: '2024-01-15',
  modificationDate: null,
  materials: ['PLA', 'ABS'],
  autoTags: ['designer:Jane Doe', 'material:PLA', 'material:ABS', 'app:OrcaSlicer'],
};

describe('ThreeMfMetadataPanel', () => {
  it('renders nothing when metadata is null', () => {
    const { container } = render(<ThreeMfMetadataPanel metadata={null} />);
    expect(container.firstChild).toBeNull();
  });

  it('renders nothing when metadata has no fields', () => {
    const emptyMetadata: ThreeMfMetadata = {
      title: null, designer: null, description: null, application: null,
      creationDate: null, modificationDate: null, materials: [], autoTags: [],
    };
    const { container } = render(<ThreeMfMetadataPanel metadata={emptyMetadata} />);
    expect(container.firstChild).toBeNull();
  });

  it('renders metadata fields', () => {
    render(<ThreeMfMetadataPanel metadata={mockMetadata} />);
    expect(screen.getByText('Test Model')).toBeInTheDocument();
    expect(screen.getByText('Jane Doe')).toBeInTheDocument();
    expect(screen.getByText('A test model')).toBeInTheDocument();
    expect(screen.getByText('OrcaSlicer')).toBeInTheDocument();
    expect(screen.getByText('2024-01-15')).toBeInTheDocument();
  });

  it('renders material badges', () => {
    render(<ThreeMfMetadataPanel metadata={mockMetadata} />);
    expect(screen.getByText('PLA')).toBeInTheDocument();
    expect(screen.getByText('ABS')).toBeInTheDocument();
  });

  it('renders suggested tags', () => {
    render(<ThreeMfMetadataPanel metadata={mockMetadata} />);
    expect(screen.getByText('Suggested Tags')).toBeInTheDocument();
    expect(screen.getByText('+ designer:Jane Doe')).toBeInTheDocument();
    expect(screen.getByText('+ material:PLA')).toBeInTheDocument();
  });

  it('filters already-applied tags from suggestions', () => {
    render(
      <ThreeMfMetadataPanel
        metadata={mockMetadata}
        existingTagNames={['designer:Jane Doe', 'material:PLA']}
      />
    );
    expect(screen.queryByText('+ designer:Jane Doe')).not.toBeInTheDocument();
    expect(screen.queryByText('+ material:PLA')).not.toBeInTheDocument();
    expect(screen.getByText('+ material:ABS')).toBeInTheDocument();
  });

  it('calls onAcceptTag when a tag is clicked', () => {
    const onAcceptTag = vi.fn();
    render(<ThreeMfMetadataPanel metadata={mockMetadata} onAcceptTag={onAcceptTag} />);
    fireEvent.click(screen.getByText('+ designer:Jane Doe'));
    expect(onAcceptTag).toHaveBeenCalledWith('designer:Jane Doe');
  });
});
