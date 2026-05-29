/**
 * ColorPaintOverlay — multi-color face-painting overlay for 3D models.
 * Unlike FacePaintOverlay (binary on/off), this supports per-face color indices
 * for multi-material / MMU painting. Each face maps to an extruder/color index.
 */
import { useRef, useState, useEffect, useCallback, useMemo } from 'react';
import { useThree, useFrame } from '@react-three/fiber';
import * as THREE from 'three';

/** Default extruder palette — matches common MMU/AMS color assignments */
const DEFAULT_EXTRUDER_COLORS: THREE.Color[] = [
  new THREE.Color('#ef4444'), // Extruder 1 — red
  new THREE.Color('#3b82f6'), // Extruder 2 — blue
  new THREE.Color('#22c55e'), // Extruder 3 — green
  new THREE.Color('#eab308'), // Extruder 4 — yellow
  new THREE.Color('#a855f7'), // Extruder 5 — purple
  new THREE.Color('#f97316'), // Extruder 6 — orange
  new THREE.Color('#06b6d4'), // Extruder 7 — cyan
  new THREE.Color('#ec4899'), // Extruder 8 — pink
];

export interface ColorPaintOverlayProps {
  meshRef: React.RefObject<THREE.Object3D | null>;
  /** Map of face index → extruder/color index (0-based) */
  paintedFaces: Map<number, number>;
  onPaintUpdate: (faces: Map<number, number>) => void;
  /** Currently active extruder/color index to paint with */
  activeColorIndex: number;
  /** Optional custom palette (defaults to DEFAULT_EXTRUDER_COLORS) */
  extruderColors?: THREE.Color[];
  opacity?: number;
  active: boolean;
  /** External paint/erase mode — when 'erase', left-click erases */
  paintMode?: 'paint' | 'erase';
  /** Brush radius in world units */
  brushSize?: number;
  /** Called when a paint stroke starts/ends on the model (for orbit control gating) */
  onPaintingStateChange?: (isPainting: boolean) => void;
}

function buildColorFaceColors(
  geometry: THREE.BufferGeometry,
  paintedFaces: Map<number, number>,
  palette: THREE.Color[],
): Float32Array {
  const posAttr = geometry.getAttribute('position');
  const index = geometry.getIndex();
  const vertexCount = posAttr.count;
  const colors = new Float32Array(vertexCount * 4);

  // Default: transparent
  for (let i = 0; i < vertexCount; i++) {
    colors[i * 4 + 3] = 0;
  }

  const triCount = index ? index.count / 3 : vertexCount / 3;
  for (let t = 0; t < triCount; t++) {
    const colorIdx = paintedFaces.get(t);
    if (colorIdx === undefined) continue;
    const color = palette[colorIdx % palette.length];
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

export function ColorPaintOverlay({
  meshRef,
  paintedFaces,
  onPaintUpdate,
  activeColorIndex,
  extruderColors,
  opacity = 0.5,
  active,
  paintMode: externalPaintMode,
  brushSize = 5,
  onPaintingStateChange,
}: ColorPaintOverlayProps) {
  const { camera, gl, invalidate } = useThree();
  const raycaster = useMemo(() => new THREE.Raycaster(), []);
  const overlayRef = useRef<THREE.Mesh>(null);
  const [hoveredFace, setHoveredFace] = useState<number | null>(null);
  const isPaintingRef = useRef(false);
  const isErasingRef = useRef(false);
  const brushIndicatorRef = useRef<THREE.Mesh>(null);

  const palette = useMemo(
    () => extruderColors ?? DEFAULT_EXTRUDER_COLORS,
    [extruderColors],
  );
  const activeColor = useMemo(
    () => palette[activeColorIndex % palette.length],
    [palette, activeColorIndex],
  );

  const paintedFacesRef = useRef(paintedFaces);
  const onPaintUpdateRef = useRef(onPaintUpdate);
  const activeColorIndexRef = useRef(activeColorIndex);
  const externalPaintModeRef = useRef(externalPaintMode);
  useEffect(() => { paintedFacesRef.current = paintedFaces; revisionRef.current += 1; }, [paintedFaces]);
  useEffect(() => { onPaintUpdateRef.current = onPaintUpdate; }, [onPaintUpdate]);
  useEffect(() => { activeColorIndexRef.current = activeColorIndex; }, [activeColorIndex]);
  useEffect(() => { externalPaintModeRef.current = externalPaintMode; }, [externalPaintMode]);

  const onPaintingStateChangeRef = useRef(onPaintingStateChange);
  useEffect(() => { onPaintingStateChangeRef.current = onPaintingStateChange; }, [onPaintingStateChange]);

  const pendingPaintRef = useRef<Map<number, number> | null>(null);
  const revisionRef = useRef(0);
  const lastRevisionRef = useRef(0);

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

  const [overlayGeometry, setOverlayGeometry] = useState<THREE.BufferGeometry | null>(null);

  useEffect(() => {
    const geo = meshRef.current?.userData.geometry ?? null;
    if (!geo) return;
    const nonIndexed = geo.index ? geo.toNonIndexed() : geo.clone();
    const vertexCount = nonIndexed.getAttribute('position').count;
    const colorData = new Float32Array(vertexCount * 4);
    nonIndexed.setAttribute('color', new THREE.BufferAttribute(colorData, 4));
    setOverlayGeometry(nonIndexed);

    return () => { nonIndexed.dispose(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const hoveredFaceRef = useRef(hoveredFace);
  useEffect(() => { hoveredFaceRef.current = hoveredFace; }, [hoveredFace]);

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

  const applyPaint = useCallback((faceIndex: number, erase: boolean) => {
    const base = pendingPaintRef.current ?? new Map(paintedFacesRef.current);
    if (erase) {
      base.delete(faceIndex);
    } else {
      base.set(faceIndex, activeColorIndexRef.current);
    }
    pendingPaintRef.current = base;
    revisionRef.current += 1;
    invalidate();
  }, [invalidate]);

  const flushPaint = useCallback(() => {
    if (pendingPaintRef.current) {
      onPaintUpdateRef.current(pendingPaintRef.current);
      pendingPaintRef.current = null;
    }
  }, []);

  useEffect(() => {
    if (!active) return;
    const el = gl.domElement;

    const capturePaintEvent = (e: PointerEvent) => {
      e.preventDefault();
      e.stopImmediatePropagation();
    };

    const onPointerDown = (e: PointerEvent) => {
      const eraseMode = externalPaintModeRef.current === 'erase';
      if (e.button === 0) {
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) {
          capturePaintEvent(e);
          isPaintingRef.current = !eraseMode;
          isErasingRef.current = eraseMode;
          applyPaint(hit.faceIndex, eraseMode);
          onPaintingStateChangeRef.current?.(true);
        }
      } else if (e.button === 2) {
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) {
          capturePaintEvent(e);
          isErasingRef.current = true;
          isPaintingRef.current = false;
          applyPaint(hit.faceIndex, true);
          onPaintingStateChangeRef.current?.(true);
        }
      }
    };

    const onPointerMove = (e: PointerEvent) => {
      const isPaintingStroke = isPaintingRef.current || isErasingRef.current;
      if (isPaintingStroke) {
        capturePaintEvent(e);
      }

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

    const onPointerUp = (e: PointerEvent) => {
      const wasPainting = isPaintingRef.current || isErasingRef.current;
      if (wasPainting) {
        capturePaintEvent(e);
      }
      isPaintingRef.current = false;
      isErasingRef.current = false;
      flushPaint();
      if (wasPainting) {
        onPaintingStateChangeRef.current?.(false);
      }
    };

    const onContextMenu = (e: Event) => { e.preventDefault(); };

    el.addEventListener('pointerdown', onPointerDown, true);
    el.addEventListener('pointermove', onPointerMove, true);
    el.addEventListener('pointerup', onPointerUp, true);
    el.addEventListener('pointerleave', onPointerUp, true);
    el.addEventListener('contextmenu', onContextMenu);

    return () => {
      el.removeEventListener('pointerdown', onPointerDown, true);
      el.removeEventListener('pointermove', onPointerMove, true);
      el.removeEventListener('pointerup', onPointerUp, true);
      el.removeEventListener('pointerleave', onPointerUp, true);
      el.removeEventListener('contextmenu', onContextMenu);
      const wasPainting = isPaintingRef.current || isErasingRef.current;
      isPaintingRef.current = false;
      isErasingRef.current = false;
      flushPaint();
      if (wasPainting) {
        onPaintingStateChangeRef.current?.(false);
      }
    };
  }, [active, applyPaint, flushPaint, gl.domElement, invalidate, raycastFace]);

  useEffect(() => {
    if (active) {
      const canvas = document.querySelector('canvas');
      if (canvas) {
        canvas.style.cursor = 'crosshair';
        return () => { canvas.style.cursor = ''; };
      }
    }
  }, [active]);

  const lastHoveredRef = useRef<number | null>(null);
  const paletteRef = useRef(palette);
  useEffect(() => { paletteRef.current = palette; }, [palette]);

  useFrame(() => {
    const obj = meshRef.current;
    const overlay = overlayRef.current;
    if (!obj || !overlay) return;
    overlay.position.copy(obj.position);
    overlay.rotation.copy(obj.rotation);
    overlay.scale.copy(obj.scale);

    if (!overlayGeometry) return;

    const hovered = hoveredFaceRef.current;
    const needsUpdate = revisionRef.current !== lastRevisionRef.current || hovered !== lastHoveredRef.current;
    if (!needsUpdate) return;
    lastRevisionRef.current = revisionRef.current;
    lastHoveredRef.current = hovered;

    const colorAttr = overlayGeometry.getAttribute('color') as THREE.BufferAttribute;
    if (!colorAttr) return;

    const faces = pendingPaintRef.current ?? paintedFacesRef.current;
    const allFaces = new Map(faces);
    if (hovered !== null && active) {
      allFaces.set(hovered, activeColorIndexRef.current);
    }
    const newColors = buildColorFaceColors(overlayGeometry, allFaces, paletteRef.current);
    colorAttr.array.set(newColors);
    colorAttr.needsUpdate = true;
  });

  if (!overlayGeometry || !active) return null;

  return (
    <group>
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

      <mesh ref={brushIndicatorRef} visible={false} renderOrder={2}>
        <sphereGeometry args={[brushSize * 0.3, 12, 12]} />
        <meshBasicMaterial
          color={activeColor}
          transparent
          opacity={0.6}
          depthTest={false}
          toneMapped={false}
        />
      </mesh>
    </group>
  );
}

export default ColorPaintOverlay;
