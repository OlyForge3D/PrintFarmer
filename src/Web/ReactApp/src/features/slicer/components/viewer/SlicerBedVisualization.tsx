/**
 * Slicer 3D Bed Visualization Component
 * A Three.js canvas showing the print bed similar to OrcaSlicer
 */
import React, { Suspense, useRef, useState, useEffect, useCallback } from 'react';
import { Canvas, useThree, useLoader } from '@react-three/fiber';
import { OrbitControls, Environment, Html, TransformControls } from '@react-three/drei';
import { STLLoader } from 'three-stdlib';
import * as THREE from 'three';
import { TextureLoader } from 'three';

export interface LoadedModel {
  id: string;
  url: string;
  fileName: string;
  fileType: 'stl' | 'ply' | '3mf';
  position: [number, number, number];
  rotation: [number, number, number];
  scale: [number, number, number];
}

export interface BedConfig {
  width: number;  // X dimension in mm
  depth: number;  // Y dimension in mm  
  height: number; // Z dimension in mm (build volume height)
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
  originCenter?: boolean; // If true, origin is at bed center; if false, at corner
}

export interface SlicerBedVisualizationProps {
  bedConfig: BedConfig;
  models?: LoadedModel[];
  selectedModelId?: string;
  onModelSelect?: (modelId: string | null) => void;
  /** Active transform tool: 'translate' | 'rotate' | 'scale' */
  transformMode?: 'translate' | 'rotate' | 'scale';
  /** Called when a model is moved/rotated/scaled via TransformControls */
  onModelTransform?: (modelId: string, position: [number, number, number], rotation: [number, number, number], scale: [number, number, number]) => void;
  showGrid?: boolean;
  showAxes?: boolean;
  gridDivisions?: number;
  backgroundColor?: string;
  className?: string;
}

/**
 * Loading indicator shown while models load
 */
function LoadingIndicator() {
  return (
    <Html center>
      <div className="bg-pf-bg-2/90 backdrop-blur-sm px-4 py-2 rounded-lg border border-pf-border shadow-lg">
        <div className="text-sm font-medium text-pf-text-primary flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" fill="none" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          Loading model...
        </div>
      </div>
    </Html>
  );
}

/**
 * Textured print bed platform component
 */
function TexturedPrintBed({ 
  width, 
  depth, 
  textureUrl 
}: { 
  width: number; 
  depth: number; 
  textureUrl: string;
}) {
  const thickness = 2;
  const meshRef = useRef<THREE.Mesh>(null);
  const texture = useLoader(TextureLoader, textureUrl);

  return (
    <mesh 
      ref={meshRef}
      position={[0, 0, -thickness / 2]} 
      receiveShadow
    >
      <boxGeometry args={[width, depth, thickness]} />
      <meshStandardMaterial 
        map={texture} 
        metalness={0.1} 
        roughness={0.5} 
      />
    </mesh>
  );
}

/**
 * Plain print bed platform component
 */
function PlainPrintBed({ 
  width, 
  depth 
}: { 
  width: number; 
  depth: number; 
}) {
  const thickness = 2;
  const meshRef = useRef<THREE.Mesh>(null);

  return (
    <mesh 
      ref={meshRef}
      position={[0, 0, -thickness / 2]} 
      receiveShadow
    >
      <boxGeometry args={[width, depth, thickness]} />
      <meshStandardMaterial 
        color="#1a1a2e"
        metalness={0.2}
        roughness={0.6}
      />
    </mesh>
  );
}

/**
 * Line-based grid overlay for the bed surface.
 * Uses actual line geometry so the bed texture is fully visible between lines.
 */
function BedGridLines({
  width,
  depth,
  cellSize = 10,
  sectionSize = 50,
}: {
  width: number;
  depth: number;
  cellSize?: number;
  sectionSize?: number;
}) {
  const gridRef = useRef<THREE.Group>(null);

  const { cellLines, sectionLines } = React.useMemo(() => {
    const halfW = width / 2;
    const halfD = depth / 2;
    const cellVerts: number[] = [];
    const sectionVerts: number[] = [];

    // Lines parallel to Y axis (varying X)
    for (let x = -halfW; x <= halfW + 0.01; x += cellSize) {
      const rounded = Math.round(x / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(rounded, -halfD, 0, rounded, halfD, 0);
    }

    // Lines parallel to X axis (varying Y)
    for (let y = -halfD; y <= halfD + 0.01; y += cellSize) {
      const rounded = Math.round(y / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(-halfW, rounded, 0, halfW, rounded, 0);
    }

    return {
      cellLines: new Float32Array(cellVerts),
      sectionLines: new Float32Array(sectionVerts),
    };
  }, [width, depth, cellSize, sectionSize]);

  return (
    <group ref={gridRef} position={[0, 0, 0.05]}>
      {/* Fine cell lines */}
      <lineSegments>
        <bufferGeometry>
          <float32BufferAttribute attach="attributes-position" args={[cellLines, 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#555577" transparent opacity={0.5} />
      </lineSegments>
      {/* Major section lines */}
      <lineSegments>
        <bufferGeometry>
          <float32BufferAttribute attach="attributes-position" args={[sectionLines, 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#7777aa" transparent opacity={0.7} />
      </lineSegments>
    </group>
  );
}

/**
 * Print bed platform with optional texture
 */
function PrintBedPlatform({ 
  width, 
  depth, 
  textureUrl, 
  textureFormat 
}: { 
  width: number; 
  depth: number; 
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
}) {
  const shouldUsePngTexture = textureUrl && textureFormat === 'png';

  return (
    <group>
      {/* Main bed surface */}
      {shouldUsePngTexture ? (
        <Suspense fallback={<PlainPrintBed width={width} depth={depth} />}>
          <TexturedPrintBed width={width} depth={depth} textureUrl={textureUrl} />
        </Suspense>
      ) : (
        <PlainPrintBed width={width} depth={depth} />
      )}

      {/* Bed edge outline */}
      <lineSegments position={[0, 0, 0.01]}>
        <edgesGeometry attach="geometry">
          <planeGeometry args={[width, depth]} />
        </edgesGeometry>
        <lineBasicMaterial color="#4a4a6a" linewidth={2} />
      </lineSegments>

      {/* Grid lines on bed surface — line-based so texture shows through */}
      <BedGridLines width={width} depth={depth} cellSize={10} sectionSize={50} />
    </group>
  );
}

/**
 * Build volume wireframe visualization
 */
function BuildVolumeWireframe({ width, depth, height }: { width: number; depth: number; height: number }) {
  return (
    <lineSegments position={[0, 0, height / 2]}>
      <edgesGeometry attach="geometry">
        <boxGeometry args={[width, depth, height]} />
      </edgesGeometry>
      <lineBasicMaterial 
        color="#00ff88" 
        linewidth={1} 
        transparent 
        opacity={0.4} 
      />
    </lineSegments>
  );
}

/**
 * Axis indicators at origin
 */
function AxisIndicators({ size = 30 }: { size?: number }) {
  return (
    <group position={[0, 0, 0.1]}>
      {/* X axis - Red */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, size, 0, 0]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#ff0000" linewidth={3} />
      </line>
      
      {/* Y axis - Green */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, 0, size, 0]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#00ff00" linewidth={3} />
      </line>
      
      {/* Z axis - Blue */}
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute 
            attach="attributes-position" 
            args={[new Float32Array([0, 0, 0, 0, 0, size]), 3]} 
          />
        </bufferGeometry>
        <lineBasicMaterial color="#0088ff" linewidth={3} />
      </line>
    </group>
  );
}

/**
 * STL Model loader component
 */
function STLModel({ 
  url, 
  position = [0, 0, 0], 
  rotation = [0, 0, 0], 
  scale = [1, 1, 1],
  selected = false,
  onClick,
  meshRef,
}: { 
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  onClick?: () => void;
  meshRef?: React.RefObject<THREE.Mesh | null>;
}) {
  const geometry = useLoader(STLLoader, url);
  const internalRef = useRef<THREE.Mesh>(null);
  const ref = meshRef || internalRef;

  // Center geometry and place on bed
  useEffect(() => {
    if (geometry) {
      geometry.computeBoundingBox();
      if (geometry.boundingBox) {
        const centerX = (geometry.boundingBox.min.x + geometry.boundingBox.max.x) / 2;
        const centerY = (geometry.boundingBox.min.y + geometry.boundingBox.max.y) / 2;
        const minZ = geometry.boundingBox.min.z;
        geometry.translate(-centerX, -centerY, -minZ);
        geometry.computeVertexNormals();
      }
    }
  }, [geometry]);

  return (
    <mesh
      ref={ref}
      geometry={geometry}
      position={position}
      rotation={rotation}
      scale={scale}
      onClick={(e) => {
        e.stopPropagation();
        onClick?.();
      }}
      castShadow
      receiveShadow
    >
      <meshStandardMaterial 
        color={selected ? "#58a6ff" : "#0969da"}
        metalness={0.15}
        roughness={0.45}
        emissive={selected ? "#0a2540" : "#041a33"}
      />
    </mesh>
  );
}

/**
 * Camera controller with optimal positioning.
 * OrbitControls ref is exposed so TransformControls can disable orbiting during drag.
 */
function CameraController({ 
  bedWidth, bedDepth, bedHeight, orbitRef 
}: { 
  bedWidth: number; bedDepth: number; bedHeight: number;
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
}) {
  const { camera } = useThree();

  useEffect(() => {
    const maxDimension = Math.max(bedWidth, bedDepth, bedHeight);
    const distance = maxDimension * 1.5;
    camera.position.set(distance * 0.7, -distance * 0.7, distance * 0.6);
    camera.up.set(0, 0, 1); // Enforce Z-up for 3D printing convention
    camera.lookAt(0, 0, bedHeight / 4);
    camera.updateProjectionMatrix();
  }, [camera, bedWidth, bedDepth, bedHeight]);

  return (
    <OrbitControls
      ref={orbitRef}
      enablePan={true}
      enableRotate={true}
      enableZoom={true}
      minDistance={50}
      maxDistance={2000}
      dampingFactor={0.05}
      rotateSpeed={0.8}
      zoomSpeed={1.2}
      minPolarAngle={0}
      maxPolarAngle={Math.PI / 2 + 0.3}
    />
  );
}

/**
 * Wrapper that attaches TransformControls to the selected model mesh.
 * Uses an effect to detect when the mesh ref becomes available.
 */
function ModelTransformControls({
  meshRef,
  mode,
  orbitRef,
  onTransformEnd,
}: {
  meshRef: React.RefObject<THREE.Mesh | null>;
  mode: 'translate' | 'rotate' | 'scale';
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
  onTransformEnd?: () => void;
}) {
  const transformRef = useRef<React.ComponentRef<typeof TransformControls>>(null);
  const [mesh, setMesh] = useState<THREE.Mesh | null>(null);

  // Sync ref → state so we re-render when the mesh appears
  useEffect(() => {
    setMesh(meshRef.current);
  }, [meshRef]);

  // Disable orbit while dragging
  useEffect(() => {
    const controls = transformRef.current;
    if (!controls) return;

    const handler = (event: { value: boolean }) => {
      if (orbitRef.current) {
        orbitRef.current.enabled = !event.value;
      }
      if (!event.value) {
        onTransformEnd?.();
      }
    };

    controls.addEventListener('dragging-changed', handler as unknown as EventListener);
    return () => {
      controls.removeEventListener('dragging-changed', handler as unknown as EventListener);
    };
  }, [orbitRef, onTransformEnd]);

  if (!mesh) return null;

  return (
    <TransformControls
      ref={transformRef}
      object={mesh}
      mode={mode}
      size={0.7}
      translationSnap={1}
      rotationSnap={THREE.MathUtils.degToRad(5)}
      scaleSnap={0.05}
    />
  );
}

/**
 * Main scene content
 */
function BedScene({
  bedConfig,
  models = [],
  selectedModelId,
  onModelSelect,
  transformMode = 'translate',
  onModelTransform,
  showAxes = true,
}: Omit<SlicerBedVisualizationProps, 'className' | 'backgroundColor' | 'showGrid' | 'gridDivisions'>) {
  const { width, depth, height, textureUrl, textureFormat } = bedConfig;
  const orbitRef = useRef<React.ComponentRef<typeof OrbitControls>>(null);
  const selectedMeshRef = useRef<THREE.Mesh>(null);

  const handleTransformEnd = useCallback(() => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const mesh = selectedMeshRef.current;
    onModelTransform(
      selectedModelId,
      mesh.position.toArray() as [number, number, number],
      [mesh.rotation.x, mesh.rotation.y, mesh.rotation.z],
      mesh.scale.toArray() as [number, number, number],
    );
  }, [selectedModelId, onModelTransform]);

  // Deselect when clicking the bed/background
  const handlePointerMissed = useCallback(() => {
    onModelSelect?.(null);
  }, [onModelSelect]);

  return (
    <>
      {/* Lighting */}
      <ambientLight intensity={0.5} />
      <directionalLight 
        position={[width, -depth, height * 2]} 
        intensity={0.8} 
        castShadow
        shadow-mapSize-width={2048}
        shadow-mapSize-height={2048}
      />
      <directionalLight position={[-width, depth, height]} intensity={0.4} />
      <pointLight position={[0, 0, height * 1.5]} intensity={0.3} />

      {/* Camera controls */}
      <CameraController bedWidth={width} bedDepth={depth} bedHeight={height} orbitRef={orbitRef} />

      {/* Print bed */}
      <PrintBedPlatform 
        width={width} 
        depth={depth} 
        textureUrl={textureUrl}
        textureFormat={textureFormat}
      />

      {/* Build volume wireframe */}
      <BuildVolumeWireframe width={width} depth={depth} height={height} />

      {/* Axis indicators */}
      {showAxes && <AxisIndicators size={Math.min(width, depth) * 0.15} />}

      {/* Loaded models */}
      <group onPointerMissed={handlePointerMissed}>
        {models.map((model) => (
          <Suspense key={model.id} fallback={<LoadingIndicator />}>
            {model.fileType === 'stl' && (
              <STLModel
                url={model.url}
                position={model.position}
                rotation={model.rotation}
                scale={model.scale}
                selected={model.id === selectedModelId}
                onClick={() => onModelSelect?.(model.id)}
                meshRef={model.id === selectedModelId ? selectedMeshRef : undefined}
              />
            )}
          </Suspense>
        ))}
      </group>

      {/* TransformControls for the selected model */}
      {selectedModelId && (
        <ModelTransformControls
          meshRef={selectedMeshRef}
          mode={transformMode}
          orbitRef={orbitRef}
          onTransformEnd={handleTransformEnd}
        />
      )}

      {/* Environment for reflections */}
      <Environment preset="studio" />
    </>
  );
}

/**
 * Main SlicerBedVisualization Component
 */
export const SlicerBedVisualization: React.FC<SlicerBedVisualizationProps> = ({
  bedConfig,
  models = [],
  selectedModelId,
  onModelSelect,
  transformMode = 'translate',
  onModelTransform,
  showAxes = true,
  backgroundColor = '#2a2a2e',
  className = '',
}) => {
  return (
    <div className={`w-full h-full ${className}`}>
      <Canvas
        style={{ background: backgroundColor }}
        gl={{
          antialias: true,
          preserveDrawingBuffer: false,
          alpha: false,
          powerPreference: 'high-performance',
        }}
        shadows
        camera={{ 
          fov: 45, 
          near: 0.1, 
          far: 10000,
          position: [300, -300, 250],
          up: [0, 0, 1] // Z-up: standard 3D printing convention
        }}
        onCreated={({ gl, scene }) => {
          gl.toneMapping = THREE.ACESFilmicToneMapping;
          gl.toneMappingExposure = 1;
          scene.background = new THREE.Color('#2a2a2e');
        }}
      >
        <Suspense fallback={null}>
          <BedScene
            bedConfig={bedConfig}
            models={models}
            selectedModelId={selectedModelId}
            onModelSelect={onModelSelect}
            transformMode={transformMode}
            onModelTransform={onModelTransform}
            showAxes={showAxes}
          />
        </Suspense>
      </Canvas>
    </div>
  );
};

export default SlicerBedVisualization;
