import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import * as THREE from 'three';

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

  // Issue #1815: the client-side proactive orientation nudge. Verifies the
  // banner wiring itself (not the pure heuristic, which is unit tested in
  // autoOrient.test.ts): it appears for a flagged model, offers the SAME
  // auto-orient action as the plate toolbar, and its dismiss control hides
  // it — clearing again once the flagged model is removed.
  describe('unslicable-orientation nudge', () => {
    // A triangular prism standing on its knife edge (identity rotation) —
    // the exact scenario from autoOrient.test.ts's "flags a tall
    // triangular-prism part" case, built as a real THREE.BufferGeometry so
    // it exercises the actual heuristic through the component, not a stub.
    function knifeEdgePrismGeometry(): THREE.BufferGeometry {
      const prism = new THREE.CylinderGeometry(15, 15, 60, 3).toNonIndexed();
      prism.rotateX(Math.PI / 2);
      prism.center();
      return prism;
    }

    it('shows a dismissible nudge for a model in a likely-unslicable orientation, and Auto-orient reuses the existing plate action', async () => {
      const onModelTransform = vi.fn();
      const a = model('A', [0, 0, 30]);

      const { rerender } = render(
        <SlicerWorkspace bedConfig={bedConfig} models={[]} onModelTransform={onModelTransform} canSlice />,
      );
      rerender(
        <SlicerWorkspace bedConfig={bedConfig} models={[a]} onModelTransform={onModelTransform} canSlice />,
      );

      const onModelGeometryChange = lastBedProps.onModelGeometryChange as (
        modelId: string,
        geometry: THREE.BufferGeometry,
      ) => void;
      act(() => onModelGeometryChange('A', knifeEdgePrismGeometry()));

      await waitFor(() => {
        expect(screen.getByText(/orientation may not print cleanly/i)).toBeInTheDocument();
      });

      // Slicing is never blocked by the nudge (advisory only).
      expect(screen.getByRole('button', { name: /slice/i })).not.toBeDisabled();

      fireEvent.click(screen.getByRole('button', { name: /auto-orient/i }));

      // The banner's action is the SAME plate-level auto-orient path
      // (handleOrientPlate), so it reports a changed rotation for the model.
      await waitFor(() => {
        expect(onModelTransform).toHaveBeenCalled();
      });
      const [, , rotation] = onModelTransform.mock.calls[0] as [string, [number, number, number], [number, number, number]];
      expect(rotation).not.toEqual([0, 0, 0]);
    });

    it('dismissing the nudge hides it, and removing the model clears the dismissal', async () => {
      const a = model('A', [0, 0, 30]);

      const { rerender } = render(
        <SlicerWorkspace bedConfig={bedConfig} models={[]} canSlice />,
      );
      rerender(<SlicerWorkspace bedConfig={bedConfig} models={[a]} canSlice />);

      const onModelGeometryChange = lastBedProps.onModelGeometryChange as (
        modelId: string,
        geometry: THREE.BufferGeometry,
      ) => void;
      act(() => onModelGeometryChange('A', knifeEdgePrismGeometry()));

      await waitFor(() => {
        expect(screen.getByText(/orientation may not print cleanly/i)).toBeInTheDocument();
      });

      fireEvent.click(screen.getByRole('button', { name: /dismiss orientation warning/i }));
      await waitFor(() => {
        expect(screen.queryByText(/orientation may not print cleanly/i)).not.toBeInTheDocument();
      });

      // Remove model A, then re-add a fresh model with the same id — the
      // dismissal was keyed to the removed instance, so the nudge should be
      // able to fire again for the new one.
      rerender(<SlicerWorkspace bedConfig={bedConfig} models={[]} canSlice />);
      rerender(<SlicerWorkspace bedConfig={bedConfig} models={[a]} canSlice />);
      act(() => onModelGeometryChange('A', knifeEdgePrismGeometry()));

      await waitFor(() => {
        expect(screen.getByText(/orientation may not print cleanly/i)).toBeInTheDocument();
      });
    });
  });

  // Regression coverage for issue #1771: the same LIBRARY model can now be
  // picked more than once, producing distinct bed-model instances (see the
  // nonce-suffixed id scheme in NewSliceJobPage.tsx). This exercises the
  // reported bug's actual path end-to-end at the plate-assignment layer:
  // create a second plate, switch to it, then add a second instance of the
  // same library model — and assert it lands on the ACTIVE (second) plate
  // with correct per-plate model counts, not just that two instances exist.
  it('placing the same library model a second time while a different plate is active assigns the new instance to that active plate', async () => {
    const onModelTransform = vi.fn();
    // First pick of library model "model-a": instance id 'model-a-0' per the
    // NewSliceJobPage `${selectedModelId}-${modelPickNonce}` id scheme.
    const firstInstance = model('model-a-0', [10, 10, 5]);

    const { rerender } = render(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[]}
        onModelTransform={onModelTransform}
      />,
    );
    // Mount empty, then add the first instance via rerender: SlicerWorkspace
    // only assigns newly-added model ids to the active plate when its
    // `models` prop *changes* after mount, not for the very first render.
    rerender(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[firstInstance]}
        onModelTransform={onModelTransform}
      />,
    );

    // Plate 1 is active and holds the first instance.
    await waitFor(() => {
      const plates = lastBedProps.plates as Array<{ id: string; active: boolean; modelIds: string[] }>;
      const plate1 = plates.find(p => p.active)!;
      expect(plate1.modelIds).toEqual(['model-a-0']);
    });

    // Create Plate 2 — it auto-activates and starts empty.
    fireEvent.click(screen.getByTitle('Add Plate'));
    await waitFor(() => {
      const plates = lastBedProps.plates as Array<{ id: string; active: boolean; modelIds: string[] }>;
      expect(plates).toHaveLength(2);
      expect(plates.find(p => p.active)!.modelIds).toEqual([]);
    });

    // Re-pick the SAME library model a second time: NewSliceJobPage now
    // generates a fresh, distinct instance id ('model-a-1') rather than
    // silently no-op'ing or reusing the existing instance.
    const secondInstance = model('model-a-1', [-10, -10, 5]);
    rerender(
      <SlicerWorkspace
        bedConfig={bedConfig}
        models={[firstInstance, secondInstance]}
        onModelTransform={onModelTransform}
      />,
    );

    await waitFor(() => {
      const plates = lastBedProps.plates as Array<{ id: string; active: boolean; modelIds: string[] }>;
      const plate1 = plates.find(p => !p.active)!;
      const plate2 = plates.find(p => p.active)!;

      // The new instance is assigned to the ACTIVE plate (Plate 2) only —
      // it must not silently stay off every plate, and it must not land
      // back on Plate 1 alongside the first instance.
      expect(plate2.modelIds).toEqual(['model-a-1']);
      // Plate 1's original instance is untouched — correct per-plate counts.
      expect(plate1.modelIds).toEqual(['model-a-0']);
    });
  });
});
