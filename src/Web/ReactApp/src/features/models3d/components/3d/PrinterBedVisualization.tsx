/**
 * Printer Bed Visualization Component
 * Real-time 3D visualization of a 3D printer's bed and nozzle position
 *
 * Features:
 * - Real-time nozzle position updates via SignalR
 * - Interactive orbit controls (rotate, pan, zoom)
 * - Automatic camera positioning
 * - Support for multiple printer bed sizes
 * - Temperature and state displays
 */

import React, { useEffect, useRef, useState, useMemo } from 'react';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { OrbitControls, PerspectiveCamera } from '@react-three/drei';
import * as THREE from 'three';
import { PrinterModelDto } from '@/types/api';
import {
  createBedVisualization,
  calculateOptimalCameraPosition,
  generateNozzleGeometry,
} from '@/common/utils/bedGeometryGenerator';

export interface PrinterStatus {
  printerId: string;
  name: string;
  state: 'Idle' | 'Printing' | 'Paused' | 'Error' | 'Offline';
  nozzlePosition?: {
    x: number;
    y: number;
    z: number;
  };
  temperatures?: {
    hotend: number;
    hotendTarget: number;
    bed: number;
    bedTarget: number;
  };
  progress?: number;
  jobName?: string;
}

export interface PrinterBedVisualizationProps {
  printerModel: PrinterModelDto;
  status: PrinterStatus;
  height?: number; // Canvas height in pixels, default 400
  autoRotate?: boolean;
  showAxes?: boolean;
  showGrid?: boolean;
}

/**
 * Nozzle Indicator Component
 * Renders a visual indicator for the nozzle position
 */
const NozzleIndicator: React.FC<{
  position: { x: number; y: number; z: number };
  nozzleDiameter?: number;
  isActive: boolean;
}> = ({ position, nozzleDiameter = 0.4, isActive }) => {
  const meshRef = useRef<THREE.Mesh>(null);

  // Create nozzle geometry
  const nozzleGeometry = generateNozzleGeometry(nozzleDiameter);

  return (
    <mesh
      ref={meshRef}
      position={[position.x, position.z, position.y]}
      geometry={nozzleGeometry}
      rotation={[Math.PI, 0, 0]} // Point downward
    >
      <meshPhongMaterial
        color={isActive ? 0xff6600 : 0xffaa33} // Darker when idle
        emissive={isActive ? 0xff4400 : 0xffaa00}
        shininess={100}
      />
    </mesh>
  );
};

/**
 * Bed Scene Component
 * Sets up the 3D scene with bed geometry and nozzle position
 */
const BedScene: React.FC<PrinterBedVisualizationProps> = ({
  printerModel,
  status,
  autoRotate = true,
  showAxes = true,
  showGrid = true,
}) => {
  const { camera } = useThree();
  const bedGroupRef = useRef<THREE.Group>(null);
  const controlsRef = useRef<InstanceType<typeof OrbitControls> | null>(null);
  const [autoRotateEnabled, setAutoRotateEnabled] = useState(autoRotate);

  // Sync autoRotate prop to state
  useEffect(() => {
    setAutoRotateEnabled(autoRotate);
  }, [autoRotate]);

  useEffect(() => {
    // Initialize scene
    if (!bedGroupRef.current) return;

    // Clear previous content
    while (bedGroupRef.current.children.length > 0) {
      const child = bedGroupRef.current.children[0];
      if (child instanceof THREE.Mesh || child instanceof THREE.LineSegments) {
        (child.geometry as THREE.BufferGeometry).dispose();
        if (Array.isArray(child.material)) {
          child.material.forEach((m) => m.dispose());
        } else {
          child.material.dispose();
        }
      }
      bedGroupRef.current.remove(child);
    }

    // Create bed visualization
    const { group: bedViz, dimensions } = createBedVisualization(printerModel);
    bedGroupRef.current.add(bedViz);

    // Set camera position
    const { position: camPos, target } = calculateOptimalCameraPosition(dimensions);
    camera.position.copy(camPos);
    camera.lookAt(target);

    // Reset orbit controls to new camera position
    if (controlsRef.current) {
      controlsRef.current.target.copy(target);
      controlsRef.current.update();
    }
  }, [printerModel, camera]);

  // Auto-rotate if enabled
  useFrame(() => {
    if (autoRotateEnabled && bedGroupRef.current && controlsRef.current) {
      controlsRef.current.autoRotate = true;
      controlsRef.current.autoRotateSpeed = 2;
    } else if (controlsRef.current) {
      controlsRef.current.autoRotate = false;
    }
  });

  const isActive = status.state === 'Printing';
  const nozzlePos = status.nozzlePosition || { x: 0, y: 0, z: 5 };

  return (
    <>
      {/* Lighting */}
      <ambientLight intensity={0.6} />
      <directionalLight position={[10, 20, 15]} intensity={0.8} castShadow />
      <directionalLight position={[-10, 15, -10]} intensity={0.4} />
      <pointLight position={[0, 30, 0]} intensity={0.3} />

      {/* Camera */}
      <PerspectiveCamera makeDefault position={[100, 80, 100]} fov={50} />

      {/* Orbit Controls */}
      <OrbitControls
        ref={controlsRef}
        enablePan
        enableRotate
        enableZoom
        autoRotate={autoRotateEnabled}
        autoRotateSpeed={2}
        dampingFactor={0.05}
        rotateSpeed={1}
        zoomSpeed={1.2}
      />

      {/* Bed visualization */}
      <group ref={bedGroupRef} />

      {/* Nozzle indicator - get nozzle diameter from primary toolhead */}
      <NozzleIndicator position={nozzlePos} isActive={isActive} nozzleDiameter={printerModel.toolheads?.find(t => t.isPrimary)?.nozzleDiameter ?? printerModel.toolheads?.[0]?.nozzleDiameter ?? 0.4} />

      {/* Grid/Axes debug helpers */}
      {showAxes && <axesHelper args={[100]} />}
      {showGrid && <gridHelper args={[200, 20]} />}
    </>
  );
};

/**
 * PrinterBedVisualization Component
 * Main component for displaying printer bed visualization
 */
export const PrinterBedVisualization: React.FC<PrinterBedVisualizationProps> = ({
  printerModel,
  status,
  height = 400,
  autoRotate = false,
  showAxes = false,
  showGrid = true,
}) => {
  // Derive error from props (no effect needed for validation)
  const error = useMemo(() => {
    if (!printerModel) {
      return 'Printer model is required';
    }
    if (!status) {
      return 'Printer status is required';
    }
    return null;
  }, [printerModel, status]);

  if (error) {
    return (
      <div
        className="rounded-lg p-4 flex items-center justify-center bg-pf-error-bg border border-pf-error"
        style={{ height: `${height}px` }}
      >
        <div className="text-center text-pf-error">
          <p className="font-semibold">Error Loading 3D Visualization</p>
          <p className="text-sm">{error}</p>
        </div>
      </div>
    );
  }

  return (
    <div className="w-full rounded-lg overflow-hidden bg-pf-bg-1 border border-pf-border">
      <Canvas
        style={{
          width: '100%',
          height: `${height}px`,
          background: '#0f0f0f',
        }}
        gl={{
          antialias: true,
          preserveDrawingBuffer: false,
          alpha: true,
        }}
      >
        <BedScene
          printerModel={printerModel}
          status={status}
          height={height}
          autoRotate={autoRotate}
          showAxes={showAxes}
          showGrid={showGrid}
        />
      </Canvas>
    </div>
  );
};

export default PrinterBedVisualization;
