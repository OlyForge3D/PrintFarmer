/**
 * Reusable face-painting overlay for 3D models.
 * Used by both Support Painting and Seam Painting tools.
 * Renders a semi-transparent colored overlay on painted faces
 * and handles raycasting for brush-based face selection.
 */
import { useRef, useState, useEffect, useCallback, useMemo } from 'react';
import { useThree, useFrame } from '@react-three/fiber';
import * as THREE from 'three';

interface FacePaintOverlayProps {
  /** Reference to the selected model's Object3D (group containing the mesh) */
  meshRef: React.RefObject<THREE.Object3D | null>;
  /** Set of painted face indices */
  paintedFaces: Set<number>;
  /** Called when faces are painted or erased */
  onPaintUpdate: (faces: Set<number>) => void;
  /** Overlay color for painted faces */
  color: THREE.ColorRepresentation;
  /** Overlay opacity */
  opacity?: number;
  /** Whether painting is currently active */
  active: boolean;
  /** External paint/erase mode — when 'erase', left-click erases instead of painting */
  paintMode?: 'paint' | 'erase';
  /** Brush radius in world units (affects multi-face hit area) */
  brushSize?: number;
  /** Called when a paint stroke starts/ends on the model (for orbit control gating) */
  onPaintingStateChange?: (isPainting: boolean) => void;
}

/**
 * Builds vertex colors for a geometry where painted face indices
 * get the overlay color and unpainted faces are fully transparent.
 */
function buildFaceColors(
  geometry: THREE.BufferGeometry,
  paintedFaces: Set<number>,
  color: THREE.Color,
): Float32Array {
  const posAttr = geometry.getAttribute('position');
  const index = geometry.getIndex();
  const vertexCount = posAttr.count;
  const colors = new Float32Array(vertexCount * 4);

  // Default: all transparent
  for (let i = 0; i < vertexCount; i++) {
    colors[i * 4 + 3] = 0; // alpha = 0
  }

  const triCount = index ? index.count / 3 : vertexCount / 3;
  for (let t = 0; t < triCount; t++) {
    if (!paintedFaces.has(t)) continue;
    const i0 = index ? index.getX(t * 3) : t * 3;
    const i1 = index ? index.getX(t * 3 + 1) : t * 3 + 1;
    const i2 = index ? index.getX(t * 3 + 2) : t * 3 + 2;
    for (const vi of [i0, i1, i2]) {
      colors[vi * 4] = color.r;
      colors[vi * 4 + 1] = color.g;
      colors[vi * 4 + 2] = color.b;
      colors[vi * 4 + 3] = 1;
    }
  }

  return colors;
}

export function FacePaintOverlay({
  meshRef,
  paintedFaces,
  onPaintUpdate,
  color,
  opacity = 0.4,
  active,
  paintMode: externalPaintMode,
  brushSize = 5,
  onPaintingStateChange,
}: FacePaintOverlayProps) {
  const { camera, gl, invalidate } = useThree();
  const raycaster = useMemo(() => new THREE.Raycaster(), []);
  const overlayRef = useRef<THREE.Mesh>(null);
  const [hoveredFace, setHoveredFace] = useState<number | null>(null);
  const isPaintingRef = useRef(false);
  const isErasingRef = useRef(false);
  const threeColor = useMemo(() => new THREE.Color(color), [color]);
  const brushIndicatorRef = useRef<THREE.Mesh>(null);

  // C4: Use refs for paint state to avoid stale closures in pointer events
  const paintedFacesRef = useRef(paintedFaces);
  const onPaintUpdateRef = useRef(onPaintUpdate);
  const externalPaintModeRef = useRef(externalPaintMode);
  useEffect(() => { paintedFacesRef.current = paintedFaces; revisionRef.current += 1; }, [paintedFaces]);
  useEffect(() => { onPaintUpdateRef.current = onPaintUpdate; }, [onPaintUpdate]);
  useEffect(() => { externalPaintModeRef.current = externalPaintMode; }, [externalPaintMode]);

  const onPaintingStateChangeRef = useRef(onPaintingStateChange);
  useEffect(() => { onPaintingStateChangeRef.current = onPaintingStateChange; }, [onPaintingStateChange]);

  // W5: Ref-based accumulator for rapid drag painting — flushes on pointerup
  const pendingPaintRef = useRef<Set<number> | null>(null);

  // C5: Monotonic revision counter for change detection instead of Set.size
  const revisionRef = useRef(0);
  const lastRevisionRef = useRef(0);

  // Get the actual mesh child from the group
  const getTargetMesh = useCallback((): THREE.Mesh | null => {
    const obj = meshRef.current;
    if (!obj) return null;
    let target: THREE.Mesh | null = null;
    obj.traverse((child) => {
      if ((child as THREE.Mesh).isMesh && !target) {
        target = child as THREE.Mesh;
      }
    });
    return target;
  }, [meshRef]);

  // Build overlay geometry — C3: convert to non-indexed to prevent vertex color bleeding
  const [overlayGeometry, setOverlayGeometry] = useState<THREE.BufferGeometry | null>(null);

  useEffect(() => {
    const geo = meshRef.current?.userData.geometry ?? null;
    if (!geo) return;
    // C3: toNonIndexed() gives each face unique vertices so painting doesn't bleed
    const nonIndexed = geo.index ? geo.toNonIndexed() : geo.clone();
    const vertexCount = nonIndexed.getAttribute('position').count;
    const colorData = new Float32Array(vertexCount * 4);
    nonIndexed.setAttribute('color', new THREE.BufferAttribute(colorData, 4));
    setOverlayGeometry(nonIndexed);

    // W3: Dispose cloned geometry on unmount
    return () => {
      nonIndexed.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hoveredFaceRef = useRef(hoveredFace);
  const threeColorRef = useRef(threeColor);
  useEffect(() => { hoveredFaceRef.current = hoveredFace; }, [hoveredFace]);
  useEffect(() => { threeColorRef.current = threeColor; }, [threeColor]);

  // Raycast helper
  const raycastFace = useCallback((clientX: number, clientY: number): { faceIndex: number; point: THREE.Vector3 } | null => {
    const mesh = getTargetMesh();
    if (!mesh) return null;

    const rect = gl.domElement.getBoundingClientRect();
    const ndc = new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1,
    );
    raycaster.setFromCamera(ndc, camera);
    const hits = raycaster.intersectObject(mesh, false);
    if (hits.length === 0 || hits[0].faceIndex === undefined) return null;
    return { faceIndex: hits[0].faceIndex, point: hits[0].point.clone() };
  }, [camera, getTargetMesh, gl.domElement, raycaster]);

  // C4+W5: Paint using ref-based accumulator — stable callback that never goes stale
  const applyPaint = useCallback((faceIndex: number, erase: boolean) => {
    // Start from pending accumulator or current painted faces
    const base = pendingPaintRef.current ?? new Set(paintedFacesRef.current);
    if (erase) {
      base.delete(faceIndex);
    } else {
      base.add(faceIndex);
    }
    pendingPaintRef.current = base;
    // C5: Bump revision on every mutation
    revisionRef.current += 1;
    invalidate();
  }, [invalidate]);

  // W5: Flush accumulated paint to parent state
  const flushPaint = useCallback(() => {
    if (pendingPaintRef.current) {
      onPaintUpdateRef.current(pendingPaintRef.current);
      pendingPaintRef.current = null;
    }
  }, []);

  // Mouse event handlers — C4: uses stable applyPaint (no paintedFaces dep)
  useEffect(() => {
    if (!active) return;
    const el = gl.domElement;

    const onPointerDown = (e: PointerEvent) => {
      const eraseMode = externalPaintModeRef.current === 'erase';
      if (e.button === 0) {
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) {
          isPaintingRef.current = !eraseMode;
          isErasingRef.current = eraseMode;
          applyPaint(hit.faceIndex, eraseMode);
          onPaintingStateChangeRef.current?.(true);
        }
        e.preventDefault();
      } else if (e.button === 2) {
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) {
          isErasingRef.current = true;
          isPaintingRef.current = false;
          applyPaint(hit.faceIndex, true);
          onPaintingStateChangeRef.current?.(true);
        }
        e.preventDefault();
      }
    };

    const onPointerMove = (e: PointerEvent) => {
      const hit = raycastFace(e.clientX, e.clientY);
      if (hit) {
        setHoveredFace(hit.faceIndex);
        if (brushIndicatorRef.current) {
          brushIndicatorRef.current.position.copy(hit.point);
          brushIndicatorRef.current.visible = true;
        }
        if (isPaintingRef.current) {
          applyPaint(hit.faceIndex, false);
        } else if (isErasingRef.current) {
          applyPaint(hit.faceIndex, true);
        }
      } else {
        setHoveredFace(null);
        if (brushIndicatorRef.current) {
          brushIndicatorRef.current.visible = false;
        }
      }
      invalidate();
    };

    const onPointerUp = () => {
      const wasPainting = isPaintingRef.current || isErasingRef.current;
      isPaintingRef.current = false;
      isErasingRef.current = false;
      // W5: Flush accumulated paint on pointer up
      flushPaint();
      if (wasPainting) {
        onPaintingStateChangeRef.current?.(false);
      }
    };

    const onContextMenu = (e: Event) => {
      e.preventDefault();
    };

    el.addEventListener('pointerdown', onPointerDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerup', onPointerUp);
    el.addEventListener('pointerleave', onPointerUp);
    el.addEventListener('contextmenu', onContextMenu);

    return () => {
      el.removeEventListener('pointerdown', onPointerDown);
      el.removeEventListener('pointermove', onPointerMove);
      el.removeEventListener('pointerup', onPointerUp);
      el.removeEventListener('pointerleave', onPointerUp);
      el.removeEventListener('contextmenu', onContextMenu);
      // Don't reset painting state during cleanup — C4 fix
    };
  }, [active, applyPaint, flushPaint, gl.domElement, invalidate, raycastFace]);

  // Cursor style
  useEffect(() => {
    if (active) {
      const canvas = document.querySelector('canvas');
      if (canvas) {
        canvas.style.cursor = 'crosshair';
        return () => { canvas.style.cursor = ''; };
      }
    }
  }, [active]);

  // Sync overlay mesh transform with the model mesh each frame and update colors
  const lastHoveredRef = useRef<number | null>(null);
  useFrame(() => {
    const obj = meshRef.current;
    const overlay = overlayRef.current;
    if (!obj || !overlay) return;
    overlay.position.copy(obj.position);
    overlay.rotation.copy(obj.rotation);
    overlay.scale.copy(obj.scale);

    if (!overlayGeometry) return;

    // C5: Use revision counter + hovered face for change detection
    const hovered = hoveredFaceRef.current;
    const needsUpdate = revisionRef.current !== lastRevisionRef.current || hovered !== lastHoveredRef.current;
    if (!needsUpdate) return;
    lastRevisionRef.current = revisionRef.current;
    lastHoveredRef.current = hovered;

    const colorAttr = overlayGeometry.getAttribute('color') as THREE.BufferAttribute;
    if (!colorAttr) return;
    // Use pending accumulator if mid-drag, otherwise use prop
    const faces = pendingPaintRef.current ?? paintedFacesRef.current;
    const allFaces = new Set(faces);
    if (hovered !== null && active) allFaces.add(hovered);
    const newColors = buildFaceColors(overlayGeometry, allFaces, threeColorRef.current);
    colorAttr.array.set(newColors);
    colorAttr.needsUpdate = true;
  });

  if (!overlayGeometry || !active) return null;

  return (
    <group>
      {/* Paint overlay mesh */}
      <mesh ref={overlayRef} geometry={overlayGeometry} renderOrder={1}>
        <meshBasicMaterial
          vertexColors
          transparent
          opacity={opacity}
          depthWrite={false}
          side={THREE.DoubleSide}
          toneMapped={false}
        />
      </mesh>

      {/* Brush cursor indicator — scales with brushSize */}
      <mesh ref={brushIndicatorRef} visible={false} renderOrder={2}>
        <sphereGeometry args={[brushSize * 0.3, 12, 12]} />
        <meshBasicMaterial
          color={color}
          transparent
          opacity={0.6}
          depthTest={false}
          toneMapped={false}
        />
      </mesh>
    </group>
  );
}

export default FacePaintOverlay;
