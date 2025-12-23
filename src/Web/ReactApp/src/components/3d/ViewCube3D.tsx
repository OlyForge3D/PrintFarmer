import React, { useRef, useEffect } from 'react';
import { useThree } from '@react-three/fiber';
import * as THREE from 'three';

interface ViewCube3DProps {
  onViewChange: (direction: 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right' | 'iso') => void;
}

/**
 * 3D View Cube Component
 * An interactive 3D cube that controls camera view orientation
 * Positioned in bottom-right corner, fully interactive
 */
export const ViewCube3D: React.FC<ViewCube3DProps> = ({ onViewChange }) => {
  const groupRef = useRef<THREE.Group>(null);
  const { camera, viewport } = useThree();

  // Position the cube in screen space (upper right corner)
  useEffect(() => {
    if (groupRef.current) {
      // Position in upper right corner relative to viewport
      // viewport.width/height are normalized canvas coordinates
      const offsetX = viewport.width / 2 - 40;
      const offsetY = viewport.height / 2 - 40;
      groupRef.current.position.set(offsetX, offsetY, 0);
    }
  }, [viewport]);

  const handleFaceClick = (face: string) => {
    const viewMap: Record<string, 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right' | 'iso'> = {
      top: 'top',
      bottom: 'bottom',
      front: 'front',
      back: 'back',
      left: 'left',
      right: 'right',
      iso: 'iso'
    };
    onViewChange(viewMap[face] || 'iso');
  };

  const cubeSize = 30;
  const faceSize = cubeSize - 2;
  const halfSize = cubeSize / 2;

  // Faces with their labels and positions
  // Positions place faces on the surface of the cube (at half the cube size from center)
  const faces = [
    { name: 'Top', label: 'T', position: [0, halfSize, 0] as [number, number, number], rotation: [-Math.PI / 2, 0, 0] as [number, number, number] },
    { name: 'Bottom', label: 'B', position: [0, -halfSize, 0] as [number, number, number], rotation: [Math.PI / 2, 0, 0] as [number, number, number] },
    { name: 'Front', label: 'F', position: [0, 0, halfSize] as [number, number, number], rotation: [0, 0, 0] as [number, number, number] },
    { name: 'Back', label: 'K', position: [0, 0, -halfSize] as [number, number, number], rotation: [0, Math.PI, 0] as [number, number, number] },
    { name: 'Left', label: 'L', position: [-halfSize, 0, 0] as [number, number, number], rotation: [0, Math.PI / 2, 0] as [number, number, number] },
    { name: 'Right', label: 'R', position: [halfSize, 0, 0] as [number, number, number], rotation: [0, -Math.PI / 2, 0] as [number, number, number] },
  ];

  return (
    <group
      ref={groupRef}
      onPointerDown={(e) => {
        e.stopPropagation();
      }}
      onPointerUp={(e) => {
        e.stopPropagation();
      }}
    >
      {/* Cube faces */}
      {faces.map((face, idx) => (
        <group key={idx} position={face.position} rotation={face.rotation}>
          <mesh
            onClick={() => handleFaceClick(face.name.toLowerCase())}
            onPointerEnter={(e) => {
              const material = (e.object as THREE.Mesh).material as THREE.MeshStandardMaterial;
              material.opacity = 0.9;
            }}
            onPointerLeave={(e) => {
              const material = (e.object as THREE.Mesh).material as THREE.MeshStandardMaterial;
              material.opacity = 0.7;
            }}
          >
            <planeGeometry args={[faceSize, faceSize]} />
            <meshStandardMaterial
              color="#3b82f6"
              opacity={0.7}
              transparent
              emissive="#1e40af"
              emissiveIntensity={0.3}
            />
          </mesh>

          {/* Face label - rendered as HTML text */}
          <mesh position={[0, 0, 1]}>
            <planeGeometry args={[20, 20]} />
            <meshStandardMaterial
              color="#ffffff"
              emissive="#ffffff"
              emissiveIntensity={1}
              transparent
              opacity={1}
            />
          </mesh>
        </group>
      ))}

      {/* Center cube edges for reference */}
      <lineSegments>
        <edgesGeometry attach="geometry">
          <boxGeometry args={[cubeSize, cubeSize, cubeSize]} />
        </edgesGeometry>
        <lineBasicMaterial attach="material" color="#64748b" linewidth={2} />
      </lineSegments>
    </group>
  );
};

export default ViewCube3D;
