import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';

// The bed visualization renders a WebGL/R3F canvas that jsdom can't run.
// Stub it so the workspace's DOM (toolbar, tabs, status bar) renders headless.
vi.mock('../SlicerBedVisualization', () => ({
  SlicerBedVisualization: () => null,
}));

import { SlicerWorkspace } from '../SlicerWorkspace';
import type { LoadedModel, BedConfig } from '../SlicerBedVisualization';

const bedConfig: BedConfig = { width: 200, depth: 200, height: 200, originCenter: true };

function model(id: string, position: [number, number, number]): LoadedModel {
  return {
    id,
    url: `https://cdn.example.com/${id}.stl`,
    fileName: `${id}.stl`,
    fileType: 'stl',
    position,
    rotation: [0, 0, 0],
    scale: [1, 1, 1],
  };
}

describe('SlicerWorkspace multi-plate', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('per-plate arrange operates on the CLICKED plate only (stale-closure lock)', async () => {
    const onModelTransform = vi.fn();
    const a = model('A', [60, 60, 5]);
    const b = model('B', [-60, -60, 5]);

    const { rerender } = render(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[a, b]}
        onModelTransform={onModelTransform}
      />,
    );

    // Plate 1 is active and holds A + B. Create Plate 2 (auto-activates, empty).
    fireEvent.click(screen.getByTitle('Add Plate'));

    // Add model C while Plate 2 is active → C lands on Plate 2.
    const c = model('C', [50, 50, 5]);
    rerender(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[a, b, c]}
        onModelTransform={onModelTransform}
      />,
    );

    // The active tab (Plate 2) exposes the inline auto-arrange action.
    const arrangeBtn = await screen.findByLabelText('Auto-arrange Plate 2');
    onModelTransform.mockClear();
    fireEvent.click(arrangeBtn);

    // Only Plate 2's model (C) is arranged — never A or B from Plate 1.
    await waitFor(() => {
      expect(onModelTransform).toHaveBeenCalledWith(
        'C',
        expect.any(Array),
        expect.any(Array),
        expect.any(Array),
        expect.objectContaining({ recordHistory: false }),
      );
    });
    const movedIds = onModelTransform.mock.calls.map(call => call[0]);
    expect(movedIds).not.toContain('A');
    expect(movedIds).not.toContain('B');
  });

  it('switching plates clears a selection that lives on a different plate', async () => {
    const onModelSelect = vi.fn();
    const a = model('A', [60, 60, 5]);

    render(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[a]}
        selectedModelId="A"
        onModelSelect={onModelSelect}
        onModelTransform={vi.fn()}
      />,
    );

    // Switch to a fresh empty plate — 'A' no longer belongs to the active plate.
    fireEvent.click(screen.getByTitle('Add Plate'));

    await waitFor(() => {
      expect(onModelSelect).toHaveBeenCalledWith(null);
    });
  });
});
