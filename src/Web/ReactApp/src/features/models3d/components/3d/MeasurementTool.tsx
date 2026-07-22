import { useRef, useState, useCallback, useEffect } from 'react';
import { useThree } from '@react-three/fiber';
import { Html, Line } from '@react-three/drei';
import * as THREE from 'three';

interface MeasurementToolProps {
  active: boolean;
  onMeasurement?: (distance: number | null) => void;
}

const MARKER_RADIUS = 0.5;
const CLICK_THRESHOLD_PX = 5;
const POINT_A_COLOR = '#ff4444';
const POINT_B_COLOR = '#4488ff';
const LINE_COLOR = '#ffaa00';

export function MeasurementTool({ active, onMeasurement }: MeasurementToolProps) {
  const [points, setPoints] = useState<THREE.Vector3[]>([]);
  const { scene, camera, gl } = useThree();
  const pointerDownPos = useRef<{ x: number; y: number } | null>(null);
  const canvasRef = useRef<HTMLElement>(gl.domElement as HTMLElement);

  // Set crosshair cursor when measurement mode is active
  useEffect(() => {
    const canvas = canvasRef.current;
    if (active) {
      canvas.style.cursor = 'crosshair';
    }
    return () => {
      canvas.style.cursor = '';
    };
  }, [active]);

  const handleMeasurementClick = useCallback(
    (point: THREE.Vector3) => {
      setPoints((prev) => {
        if (prev.length >= 2) {
          onMeasurement?.(null);
          return [point];
        }
        const next = [...prev, point];
        if (next.length === 2) {
          onMeasurement?.(next[0].distanceTo(next[1]));
        }
        return next;
      });
    },
    [onMeasurement],
  );

  // Attach pointer listeners to distinguish clicks from orbit drags
  useEffect(() => {
    if (!active) return;

    const canvas = gl.domElement;

    const onPointerDown = (e: PointerEvent) => {
      pointerDownPos.current = { x: e.clientX, y: e.clientY };
    };

    const onPointerUp = (e: PointerEvent) => {
      if (!pointerDownPos.current) return;
      const dx = e.clientX - pointerDownPos.current.x;
      const dy = e.clientY - pointerDownPos.current.y;
      const dist = Math.sqrt(dx * dx + dy * dy);
      pointerDownPos.current = null;

      if (dist >= CLICK_THRESHOLD_PX) return;

      const rect = canvas.getBoundingClientRect();
      const ndcX = ((e.clientX - rect.left) / rect.width) * 2 - 1;
      const ndcY = -((e.clientY - rect.top) / rect.height) * 2 + 1;

      const raycaster = new THREE.Raycaster();
      raycaster.setFromCamera(new THREE.Vector2(ndcX, ndcY), camera);

      const meshes: THREE.Object3D[] = [];
      scene.traverse((obj) => {
        if (
          obj instanceof THREE.Mesh &&
          !obj.userData.isMeasurementHelper
        ) {
          meshes.push(obj);
        }
      });

      const intersects = raycaster.intersectObjects(meshes, false);
      if (intersects.length > 0) {
        handleMeasurementClick(intersects[0].point.clone());
      }
    };

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointerup', onPointerUp);
    return () => {
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointerup', onPointerUp);
    };
  }, [active, gl, camera, scene, handleMeasurementClick]);

  if (!active) return null;

  const distance =
    points.length === 2 ? points[0].distanceTo(points[1]) : null;

  const midpoint =
    points.length === 2
      ? new THREE.Vector3()
          .addVectors(points[0], points[1])
          .multiplyScalar(0.5)
      : null;

  return (
    <>
      {/* Point markers */}
      {points.map((pt, i) => (
        <mesh
          key={i}
          position={pt}
          userData={{ isMeasurementHelper: true }}
        >
          <sphereGeometry args={[MARKER_RADIUS, 16, 16]} />
          <meshBasicMaterial
            color={i === 0 ? POINT_A_COLOR : POINT_B_COLOR}
            depthTest={false}
          />
        </mesh>
      ))}

      {/* Dashed line between points */}
      {points.length === 2 && (
        <Line
          points={[points[0], points[1]]}
          color={LINE_COLOR}
          lineWidth={2}
          dashed
          dashSize={2}
          gapSize={1}
          userData={{ isMeasurementHelper: true }}
        />
      )}

      {/* Distance label at midpoint */}
      {distance !== null && midpoint && (
        <Html
          position={midpoint}
          center
          style={{ pointerEvents: 'none' }}
          userData={{ isMeasurementHelper: true }}
        >
          <div className="rounded border border-pf-border bg-pf-bg-2/90 px-2 py-1 font-mono text-xs text-pf-text-primary shadow-lg whitespace-nowrap">
            {distance.toFixed(2)} mm
          </div>
        </Html>
      )}
    </>
  );
}
