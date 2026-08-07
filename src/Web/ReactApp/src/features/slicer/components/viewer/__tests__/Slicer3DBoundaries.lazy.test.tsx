import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

const moduleLoads = vi.hoisted(() => ({
  workspace: vi.fn(),
  preview: vi.fn(),
}));

vi.mock('@/features/slicer/components/viewer/SlicerWorkspace', () => {
  moduleLoads.workspace();
  return {
    SlicerWorkspace: () => <div data-testid="slicer-workspace-mock">Workspace</div>,
  };
});

vi.mock('@/features/models3d/components/3d/STLPreviewModal', () => {
  moduleLoads.preview();
  return {
    STLPreviewModal: () => <div role="dialog" aria-label="STL preview mock">Preview</div>,
  };
});

import { SlicerWorkspaceBoundary } from '../SlicerWorkspaceBoundary';
import { STLPreviewModalBoundary } from '../STLPreviewModalBoundary';

describe('slicer 3D lazy boundaries', () => {
  it('does not load the workspace or preview modules before their feature mounts', async () => {
    const { rerender } = render(<div>No 3D feature selected</div>);

    expect(moduleLoads.workspace).not.toHaveBeenCalled();
    expect(moduleLoads.preview).not.toHaveBeenCalled();

    rerender(<SlicerWorkspaceBoundary bedConfig={{ width: 220, depth: 220, height: 250 }} />);
    expect(await screen.findByTestId('slicer-workspace-mock')).toBeInTheDocument();
    expect(moduleLoads.workspace).toHaveBeenCalledTimes(1);
    expect(moduleLoads.preview).not.toHaveBeenCalled();

    rerender(
      <STLPreviewModalBoundary
        isOpen
        fileUrl="/api/3d-models/file/model-1"
        fileName="part.stl"
        onClose={vi.fn()}
      />,
    );
    expect(await screen.findByRole('dialog', { name: 'STL preview mock' })).toBeInTheDocument();
    expect(moduleLoads.preview).toHaveBeenCalledTimes(1);
  });
});
