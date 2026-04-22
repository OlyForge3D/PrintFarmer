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
}: FacePaintOverlayProps) {
  const { camera, gl, invalidate } = useThree();
  const raycaster = useMemo(() => new THREE.Raycaster(), []);
  const overlayRef = useRef<THREE.Mesh>(null);
  const [hoveredFace, setHoveredFace] = useState<number | null>(null);
  const isPaintingRef = useRef(false);
  const isErasingRef = useRef(false);
  const threeColor = useMemo(() => new THREE.Color(color), [color]);
  const brushIndicatorRef = useRef<THREE.Mesh>(null);

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

  // Build overlay geometry — use lazy initializer since component remounts on mode toggle
  const [overlayGeometry, setOverlayGeometry] = useState<THREE.BufferGeometry | null>(null);
  const overlayGeometryInitRef = useRef(false);

  // Initialize geometry once after mount (avoids setState-in-render)
  useEffect(() => {
    if (overlayGeometryInitRef.current) return;
    overlayGeometryInitRef.current = true;
    const geo = meshRef.current?.userData.geometry ?? null;
    if (!geo) return;
    const clone = geo.clone();
    const vertexCount = clone.getAttribute('position').count;
    const colorData = new Float32Array(vertexCount * 4);
    clone.setAttribute('color', new THREE.BufferAttribute(colorData, 4));
    setOverlayGeometry(clone);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Sync prop/state values into refs for use inside useFrame (avoids ref-during-render lint)
  const paintedFacesRef = useRef(paintedFaces);
  const hoveredFaceRef = useRef(hoveredFace);
  const threeColorRef = useRef(threeColor);
  useEffect(() => { paintedFacesRef.current = paintedFaces; }, [paintedFaces]);
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

  // Paint or erase a face
  const applyPaint = useCallback((faceIndex: number, erase: boolean) => {
    const next = new Set(paintedFaces);
    if (erase) {
      next.delete(faceIndex);
    } else {
      next.add(faceIndex);
    }
    onPaintUpdate(next);
  }, [onPaintUpdate, paintedFaces]);

  // Mouse event handlers
  useEffect(() => {
    if (!active) return;
    const el = gl.domElement;

    const onPointerDown = (e: PointerEvent) => {
      if (e.button === 0) {
        // Left click = paint
        isPaintingRef.current = true;
        isErasingRef.current = false;
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) applyPaint(hit.faceIndex, false);
        e.preventDefault();
      } else if (e.button === 2) {
        // Right click = erase
        isErasingRef.current = true;
        isPaintingRef.current = false;
        const hit = raycastFace(e.clientX, e.clientY);
        if (hit) applyPaint(hit.faceIndex, true);
        e.preventDefault();
      }
    };

    const onPointerMove = (e: PointerEvent) => {
      const hit = raycastFace(e.clientX, e.clientY);
      if (hit) {
        setHoveredFace(hit.faceIndex);
        // Update brush indicator position
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
      isPaintingRef.current = false;
      isErasingRef.current = false;
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
      isPaintingRef.current = false;
      isErasingRef.current = false;
    };
  }, [active, applyPaint, gl.domElement, invalidate, raycastFace]);

  // Cursor style — use direct DOM access to avoid hook immutability lint
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
  const lastPaintedCountRef = useRef(0);
  const lastHoveredRef = useRef<number | null>(null);
  useFrame(() => {
    const obj = meshRef.current;
    const overlay = overlayRef.current;
    if (!obj || !overlay) return;
    overlay.position.copy(obj.position);
    overlay.rotation.copy(obj.rotation);
    overlay.scale.copy(obj.scale);

    // Update vertex colors when paint data or hover changes
    if (!overlayGeometry) return;
    const faces = paintedFacesRef.current;
    const hovered = hoveredFaceRef.current;
    const needsUpdate = faces.size !== lastPaintedCountRef.current || hovered !== lastHoveredRef.current;
    if (!needsUpdate) return;
    lastPaintedCountRef.current = faces.size;
    lastHoveredRef.current = hovered;

    const colorAttr = overlayGeometry.getAttribute('color') as THREE.BufferAttribute;
    if (!colorAttr) return;
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

      {/* Brush cursor indicator */}
      <mesh ref={brushIndicatorRef} visible={false} renderOrder={2}>
        <sphereGeometry args={[1.5, 12, 12]} />
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
