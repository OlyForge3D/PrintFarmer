/**
 * Clearance Zone Overlay — React Three Fiber component that renders
 * semi-transparent boxes on the bed showing printhead clearance zones
 * for sequential (by-object) printing.
 */
import { useEffect, useMemo, useRef } from 'react';
import * as THREE from 'three';
import type {
  ClearanceZone,
  CollisionResult,
  ModelFootprint,
} from '../../utils/sequentialPrinting';

export interface ClearanceZoneOverlayProps {
  zones: ClearanceZone[];
  collisions: CollisionResult[];
  models: ModelFootprint[];
  clearanceHeight: number;
  visible: boolean;
}

const CLEAR_COLOR = new THREE.Color(0x00bcd4);
const COLLISION_COLOR = new THREE.Color(0xf44336);
const CLEAR_OPACITY = 0.12;
const COLLISION_OPACITY = 0.22;

/** Memoized edges geometry that properly disposes its source BoxGeometry. */
function ZoneEdges({ width, depth, height }: { width: number; depth: number; height: number }) {
  const boxGeo = useMemo(() => new THREE.BoxGeometry(width, depth, height), [width, depth, height]);
  const edgesGeo = useMemo(() => new THREE.EdgesGeometry(boxGeo), [boxGeo]);
  const prevBoxRef = useRef<THREE.BoxGeometry | null>(null);
  const prevEdgesRef = useRef<THREE.EdgesGeometry | null>(null);

  useEffect(() => {
    return () => {
      boxGeo.dispose();
      edgesGeo.dispose();
    };
  }, [boxGeo, edgesGeo]);

  // Dispose previous geometries when deps change
  useEffect(() => {
    if (prevBoxRef.current && prevBoxRef.current !== boxGeo) prevBoxRef.current.dispose();
    if (prevEdgesRef.current && prevEdgesRef.current !== edgesGeo) prevEdgesRef.current.dispose();
    prevBoxRef.current = boxGeo;
    prevEdgesRef.current = edgesGeo;
  }, [boxGeo, edgesGeo]);

  return <primitive object={edgesGeo} attach="geometry" />;
}

/** Memoized fill geometry that properly disposes its BoxGeometry on change. */
function ZoneFill({ width, depth, height, color, opacity }: { width: number; depth: number; height: number; color: THREE.Color; opacity: number }) {
  const geometry = useMemo(() => new THREE.BoxGeometry(width, depth, height), [width, depth, height]);

  useEffect(() => {
    return () => { geometry.dispose(); };
  }, [geometry]);

  return (
    <mesh>
      <primitive object={geometry} attach="geometry" />
      <meshBasicMaterial color={color} transparent opacity={opacity} side={THREE.DoubleSide} depthWrite={false} toneMapped={false} />
    </mesh>
  );
}

export function ClearanceZoneOverlay({
  zones,
  collisions,
  models,
  clearanceHeight,
  visible,
}: ClearanceZoneOverlayProps) {
  const collidingIds = useMemo(() => {
    const ids = new Set<string>();
    for (const c of collisions) {
      ids.add(c.modelA);
      ids.add(c.modelB);
    }
    return ids;
  }, [collisions]);

  const modelHeightMap = useMemo(
    () => new Map(models.map((m) => [m.modelId, m.height])),
    [models],
  );

  if (!visible || zones.length === 0) return null;

  return (
    <group>
      {zones.map((zone) => {
        const width = zone.maxX - zone.minX;
        const depth = zone.maxY - zone.minY;
        const modelHeight = modelHeightMap.get(zone.modelId) ?? 0;
        const boxHeight = Math.max(modelHeight, clearanceHeight);
        const centerX = (zone.minX + zone.maxX) / 2;
        const centerY = (zone.minY + zone.maxY) / 2;
        const isColliding = collidingIds.has(zone.modelId);
        const color = isColliding ? COLLISION_COLOR : CLEAR_COLOR;
        const opacity = isColliding ? COLLISION_OPACITY : CLEAR_OPACITY;

        return (
          <group
            key={zone.modelId}
            position={[centerX, centerY, boxHeight / 2]}
            renderOrder={1}
          >
            <ZoneFill width={width} depth={depth} height={boxHeight} color={color} opacity={opacity} />
          </group>
        );
      })}

      {/* Wireframe outlines for better visibility */}
      {zones.map((zone) => {
        const width = zone.maxX - zone.minX;
        const depth = zone.maxY - zone.minY;
        const modelHeight = modelHeightMap.get(zone.modelId) ?? 0;
        const boxHeight = Math.max(modelHeight, clearanceHeight);
        const centerX = (zone.minX + zone.maxX) / 2;
        const centerY = (zone.minY + zone.maxY) / 2;
        const isColliding = collidingIds.has(zone.modelId);
        const color = isColliding ? COLLISION_COLOR : CLEAR_COLOR;

        return (
          <lineSegments
            key={`wire-${zone.modelId}`}
            position={[centerX, centerY, boxHeight / 2]}
            renderOrder={2}
          >
            <ZoneEdges width={width} depth={depth} height={boxHeight} />
            <lineBasicMaterial color={color} transparent opacity={0.5} />
          </lineSegments>
        );
      })}
    </group>
  );
}

export default ClearanceZoneOverlay;
