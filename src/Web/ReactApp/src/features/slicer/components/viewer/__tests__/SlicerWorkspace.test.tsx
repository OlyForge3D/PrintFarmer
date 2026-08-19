import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import '@testing-library/jest-dom';

const { mockToast } = vi.hoisted(() => ({
  mockToast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
}));
vi.mock('sonner', () => ({ toast: mockToast }));

// The bed visualization renders a WebGL/R3F canvas that jsdom can't run.
// Stub it but CAPTURE the props the workspace passes in, so tests can invoke the
// per-plate callbacks (arrange/orient/delete) that now live in the in-scene
// PlateBedOverlay (rendered inside the bed, hence unavailable in jsdom).
let lastBedProps: Record<string, unknown> = {};
vi.mock('../SlicerBedVisualization', () => ({
  SlicerBedVisualization: (props: Record<string, unknown>) => {
    lastBedProps = props;
    return null;
  },
}));

import { SlicerWorkspace } from '../SlicerWorkspace';
import type { LoadedModel, BedConfig } from '../SlicerBedVisualization';

interface ScenePlateLike { id: string; active: boolean }

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
    lastBedProps = {};
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

    // Invoke the per-plate arrange handler (lives in the in-scene overlay) for the
    // ACTIVE plate (Plate 2) via the captured bed props.
    const plates = lastBedProps.plates as ScenePlateLike[];
    const activePlate = plates.find(p => p.active)!;
    const onPlateArrange = lastBedProps.onPlateArrange as (id: string) => void;
    onModelTransform.mockClear();
    act(() => onPlateArrange(activePlate.id));

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

  it('per-plate arrange is a no-op on a LOCKED plate', async () => {
    const onModelTransform = vi.fn();
    const a = model('A', [60, 60, 5]);

    render(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[a]}
        onModelTransform={onModelTransform}
      />,
    );

    // Lock the active plate via the captured per-plate lock toggle, then attempt
    // to arrange it — the workspace must refuse to transform a locked plate.
    const plates = lastBedProps.plates as ScenePlateLike[];
    const active = plates.find(p => p.active)!;
    const onToggleLock = lastBedProps.onPlateToggleLock as (id: string) => void;
    const onPlateArrange = lastBedProps.onPlateArrange as (id: string) => void;

    act(() => onToggleLock(active.id));
    await waitFor(() => {
      const next = (lastBedProps.plates as Array<{ id: string; locked: boolean }>).find(p => p.id === active.id);
      expect(next?.locked).toBe(true);
    });

    onModelTransform.mockClear();
    act(() => onPlateArrange(active.id));
    expect(onModelTransform).not.toHaveBeenCalled();
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

  // Regression coverage for issue #1709: "Slice Plate" remained enabled and
  // threw after a model on the active plate failed to load its source file.
  describe('slice action after a model load failure', () => {
    it('disables Slice and blocks the click with an actionable message once a model on the active plate fails to load', async () => {
      const onSlice = vi.fn();
      const a = model('A', [60, 60, 5]);

      // Mount empty, then add the model via rerender: SlicerWorkspace only
      // assigns newly-added model ids to the active plate when its `models`
      // prop *changes* after mount, not for the very first render's models.
      const { rerender } = render(
        <SlicerWorkspace bedConfig={bedConfig} models={[]} onSlice={onSlice} canSlice />,
      );
      rerender(<SlicerWorkspace bedConfig={bedConfig} models={[a]} onSlice={onSlice} canSlice />);

      // Slice is enabled before any load failure is reported.
      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      // The bed visualization reports the model's source file failed to load.
      const onModelLoadError = lastBedProps.onModelLoadError as (modelId: string) => void;
      act(() => onModelLoadError('A'));

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).toBeDisabled();
      });
      expect(screen.getByText(/model on this plate failed to load/i)).toBeInTheDocument();

      // Since the Slice button is now disabled, a real click cannot reach the
      // handler at all — the browser doesn't dispatch click on disabled
      // buttons, and this is itself the crash-avoidance the fix guarantees.
      fireEvent.click(screen.getByRole('button', { name: /slice/i }));
      expect(onSlice).not.toHaveBeenCalled();
    });

    it('re-enables Slice once the model reloads successfully', async () => {
      const onSlice = vi.fn();
      const a = model('A', [60, 60, 5]);

      const { rerender } = render(
        <SlicerWorkspace bedConfig={bedConfig} models={[]} onSlice={onSlice} canSlice />,
      );
      rerender(<SlicerWorkspace bedConfig={bedConfig} models={[a]} onSlice={onSlice} canSlice />);
      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      const onModelLoadError = lastBedProps.onModelLoadError as (modelId: string) => void;
      act(() => onModelLoadError('A'));
      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).toBeDisabled();
      });

      // A successful (re)load reports non-null geometry for the same model id.
      const onModelGeometryChange = lastBedProps.onModelGeometryChange as (
        modelId: string,
        geometry: unknown,
      ) => void;
      act(() => onModelGeometryChange('A', {}));

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();
      });

      fireEvent.click(screen.getByRole('button', { name: /slice/i }));
      expect(onSlice).toHaveBeenCalledWith(['A']);
    });
  });
});
