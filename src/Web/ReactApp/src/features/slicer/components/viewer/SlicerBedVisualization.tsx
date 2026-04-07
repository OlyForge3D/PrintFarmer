/**
 * Slicer 3D Bed Visualization Component
 * A Three.js canvas showing the print bed similar to OrcaSlicer
 */
import React, { Component, Suspense, useRef, useState, useEffect, useCallback, useMemo } from 'react';
import type { ReactNode, ErrorInfo } from 'react';
import { Canvas, useThree, useLoader, useFrame } from '@react-three/fiber';
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
  transformMode?: 'translate' | 'rotate' | 'scale' | null;
  /** Called when a model is moved/rotated/scaled via TransformControls */
  onModelTransform?: (
    modelId: string,
    position: [number, number, number],
    rotation: [number, number, number],
    scale: [number, number, number],
    options?: {
      recordHistory?: boolean;
      actionLabel?: string;
      historyBefore?: {
        position: [number, number, number];
        rotation: [number, number, number];
        scale: [number, number, number];
      };
    },
  ) => void;
  /** Called with current selected model sizing information for scale UI */
  onSelectedModelMetricsChange?: (metrics: {
    modelId: string;
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  } | null) => void;
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
 * Error boundary that catches texture load failures and falls back to plain bed.
 * Required because useLoader(TextureLoader, url) throws when the file is missing (404).
 */
interface TextureFallbackBoundaryProps {
  children: ReactNode;
  fallback: ReactNode;
}
interface TextureFallbackBoundaryState {
  hasError: boolean;
}
class TextureFallbackBoundary extends Component<TextureFallbackBoundaryProps, TextureFallbackBoundaryState> {
  state: TextureFallbackBoundaryState = { hasError: false };
  static getDerivedStateFromError(): TextureFallbackBoundaryState {
    return { hasError: true };
  }
  componentDidCatch(error: Error, info: ErrorInfo) {
    console.warn('[SlicerBed] Bed texture load failed, using plain bed:', error.message, info.componentStack);
  }
  render() {
    return this.state.hasError ? this.props.fallback : this.props.children;
  }
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
        <TextureFallbackBoundary fallback={<PlainPrintBed width={width} depth={depth} />}>
          <Suspense fallback={<PlainPrintBed width={width} depth={depth} />}>
            <TexturedPrintBed width={width} depth={depth} textureUrl={textureUrl} />
          </Suspense>
        </TextureFallbackBoundary>
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
 * Wireframe bounding box rendered around the selected model.
 * Positioned in the mesh's local coordinate space so it inherits
 * all parent transforms (position, rotation, scale) automatically.
 */
function SelectionBoundingBox({ geometry }: { geometry: THREE.BufferGeometry }) {
  const PADDING = 2; // visual padding around the model so the box is clearly outside surfaces
  const CORNER_LENGTH = 10; // length of corner indicator lines
  
  const { center, cornerLines } = useMemo(() => {
    const box = new THREE.Box3();
    geometry.computeBoundingBox();
    if (geometry.boundingBox) {
      box.copy(geometry.boundingBox);
    }
    const s = new THREE.Vector3();
    const c = new THREE.Vector3();
    box.getSize(s);
    box.getCenter(c);
    
    // Calculate padded dimensions
    const hsX = (s.x + PADDING * 2) / 2;
    const hsY = (s.y + PADDING * 2) / 2;
    const hsZ = (s.z + PADDING * 2) / 2;
    
    // 8 corners of the bounding box (in local coords)
    const corners: [number, number, number][] = [
      [-hsX, -hsY, -hsZ], [hsX, -hsY, -hsZ], [hsX, hsY, -hsZ], [-hsX, hsY, -hsZ],  // bottom 4
      [-hsX, -hsY, hsZ], [hsX, -hsY, hsZ], [hsX, hsY, hsZ], [-hsX, hsY, hsZ],    // top 4
    ];
    
    // Create line segments (corner brackets): 3 lines per corner
    const lines: [number, number, number, number, number, number][] = [];
    
    corners.forEach(([x, y, z]) => {
      // Line along X axis (inward)
      lines.push([x, y, z, x + (x > 0 ? -CORNER_LENGTH : CORNER_LENGTH), y, z]);
      // Line along Y axis (inward)
      lines.push([x, y, z, x, y + (y > 0 ? -CORNER_LENGTH : CORNER_LENGTH), z]);
      // Line along Z axis (inward)
      lines.push([x, y, z, x, y, z + (z > 0 ? -CORNER_LENGTH : CORNER_LENGTH)]);
    });
    
    return {
      center: [c.x, c.y, c.z] as [number, number, number],
      cornerLines: lines,
    };
  }, [geometry]);

  const lineSegmentsRef = useRef<THREE.LineSegments>(null);
  
  // Build geometry from corner lines
  useEffect(() => {
    if (!lineSegmentsRef.current) return;
    
    const positions: number[] = [];
    cornerLines.forEach(([x1, y1, z1, x2, y2, z2]) => {
      positions.push(x1, y1, z1, x2, y2, z2);
    });
    
    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(new Float32Array(positions), 3));
    lineSegmentsRef.current.geometry = geom;
  }, [cornerLines]);

  return (
    <lineSegments ref={lineSegmentsRef} position={center} renderOrder={1000}>
      <lineBasicMaterial
        color="#ffffff"
        linewidth={1}
        transparent
        opacity={1}
        depthTest={false}
        toneMapped={false}
      />
    </lineSegments>
  );
}

/**
 * STL Model loader component.
 *
 * Selection uses onPointerDown so the hit registers immediately on press,
 * before OrbitControls can interpret the gesture as an orbit drag.  The
 * companion onClick handler is kept so R3F still considers the mesh
 * "interactive" for its click bookkeeping (prevents onPointerMissed from
 * firing when a mesh was actually pressed).
 */
function STLModel({ 
  url, 
  position = [0, 0, 0], 
  rotation = [0, 0, 0], 
  scale = [1, 1, 1],
  selected = false,
  onClick,
  meshRef,
  onSelectedMetrics,
}: { 
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  onClick?: () => void;
  meshRef?: React.RefObject<THREE.Mesh | null>;
  onSelectedMetrics?: (metrics: {
    baseSize: [number, number, number];
    currentSize: [number, number, number];
    currentScale: [number, number, number];
  }) => void;
}) {
  const rawGeometry = useLoader(STLLoader, url);
  const internalRef = useRef<THREE.Mesh>(null);
  const ref = meshRef || internalRef;

  // Clone geometry so we don't mutate the useLoader cache, center it on the
  // bed, and recompute bounding sphere so raycasting (click-to-select) works.
  const geometry = useMemo(() => {
    const geo = rawGeometry.clone();
    geo.computeBoundingBox();
    if (geo.boundingBox) {
      const centerX = (geo.boundingBox.min.x + geo.boundingBox.max.x) / 2;
      const centerY = (geo.boundingBox.min.y + geo.boundingBox.max.y) / 2;
      const minZ = geo.boundingBox.min.z;
      geo.translate(-centerX, -centerY, -minZ);
    }
    geo.computeVertexNormals();
    geo.computeBoundingBox();
    geo.computeBoundingSphere();
    return geo;
  }, [rawGeometry]);

  const baseSize = useMemo<[number, number, number]>(() => {
    geometry.computeBoundingBox();
    if (!geometry.boundingBox) {
      return [0, 0, 0];
    }

    const size = new THREE.Vector3();
    geometry.boundingBox.getSize(size);
    return [size.x, size.y, size.z];
  }, [geometry]);

  useEffect(() => {
    if (!selected || !onSelectedMetrics) return;

    onSelectedMetrics({
      baseSize,
      currentSize: [
        baseSize[0] * scale[0],
        baseSize[1] * scale[1],
        baseSize[2] * scale[2],
      ],
      currentScale: scale,
    });
  }, [baseSize, onSelectedMetrics, scale, selected]);

  return (
    <mesh
      ref={ref}
      geometry={geometry}
      position={position}
      rotation={rotation}
      scale={scale}
      onPointerDown={(e) => {
        e.stopPropagation();
        onClick?.();
      }}
      onClick={(e) => {
        e.stopPropagation();
        onClick?.();
      }}
      castShadow
      receiveShadow
    >
      <meshStandardMaterial 
        color="#009688"
        metalness={0.15}
        roughness={0.45}
        emissive="#002b26"
      />
      {selected && <SelectionBoundingBox geometry={geometry} />}
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
      makeDefault
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
  onTransform,
  onTransformStart,
  onTransformEnd,
}: {
  meshRef: React.RefObject<THREE.Mesh | null>;
  mode: 'translate' | 'rotate' | 'scale';
  orbitRef: React.RefObject<React.ComponentRef<typeof OrbitControls> | null>;
  onTransform?: () => void;
  onTransformStart?: () => void;
  onTransformEnd?: () => void;
}) {
  const transformRef = useRef<React.ComponentRef<typeof TransformControls>>(null);
  const [mesh, setMesh] = useState<THREE.Mesh | null>(null);

  // Sync ref → state via animation frame: the mesh ref is set by STLModel
  // after Suspense resolves, which may be after this component mounts.
  // useEffect([meshRef]) never re-fires because ref identity is stable.
  useFrame(() => {
    const current = meshRef.current;
    if (current !== mesh) {
      setMesh(current);
    }
  });

  // Disable orbit while dragging
  useEffect(() => {
    const controls = transformRef.current;
    if (!controls) return;
    const controlsAny = controls as unknown as {
      addEventListener: (type: string, listener: EventListener) => void;
      removeEventListener: (type: string, listener: EventListener) => void;
    };

    const handler = (event: { value: boolean }) => {
      if (orbitRef.current) {
        orbitRef.current.enabled = !event.value;
      }
      if (event.value) {
        onTransformStart?.();
      } else {
        onTransformEnd?.();
      }
    };

    controlsAny.addEventListener('dragging-changed', handler as unknown as EventListener);
    return () => {
      controlsAny.removeEventListener('dragging-changed', handler as unknown as EventListener);
    };
  }, [onTransformEnd, onTransformStart, orbitRef]);

  useEffect(() => {
    const controls = transformRef.current;
    if (!controls) return;
    const controlsAny = controls as unknown as {
      addEventListener: (type: string, listener: EventListener) => void;
      removeEventListener: (type: string, listener: EventListener) => void;
    };

    const handler = () => {
      onTransform?.();
    };

    controlsAny.addEventListener('objectChange', handler as unknown as EventListener);
    return () => {
      controlsAny.removeEventListener('objectChange', handler as unknown as EventListener);
    };
  }, [onTransform]);

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
  transformMode,
  onModelTransform,
  onSelectedModelMetricsChange,
  showAxes = true,
}: Omit<SlicerBedVisualizationProps, 'className' | 'backgroundColor' | 'showGrid' | 'gridDivisions'>) {
  const { width, depth, height, textureUrl, textureFormat } = bedConfig;
  const orbitRef = useRef<React.ComponentRef<typeof OrbitControls>>(null);
  const selectedMeshRef = useRef<THREE.Mesh>(null);

  const dragStartTransformRef = useRef<{
    position: [number, number, number];
    rotation: [number, number, number];
    scale: [number, number, number];
  } | null>(null);

  const transformActionLabel = transformMode === 'translate'
    ? 'Move Model'
    : transformMode === 'rotate'
      ? 'Rotate Model'
      : 'Scale Model';

  const handleTransformStart = useCallback(() => {
    if (!selectedMeshRef.current) return;
    const mesh = selectedMeshRef.current;
    dragStartTransformRef.current = {
      position: mesh.position.toArray() as [number, number, number],
      rotation: [mesh.rotation.x, mesh.rotation.y, mesh.rotation.z],
      scale: mesh.scale.toArray() as [number, number, number],
    };
  }, []);

  const handleTransformEnd = useCallback(() => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const mesh = selectedMeshRef.current;
    onModelTransform(
      selectedModelId,
      mesh.position.toArray() as [number, number, number],
      [mesh.rotation.x, mesh.rotation.y, mesh.rotation.z],
      mesh.scale.toArray() as [number, number, number],
      {
        recordHistory: true,
        actionLabel: transformActionLabel,
        historyBefore: dragStartTransformRef.current ?? undefined,
      },
    );
    dragStartTransformRef.current = null;
  }, [onModelTransform, selectedModelId, transformActionLabel]);

  const handleTransformChange = useCallback(() => {
    if (!selectedModelId || !selectedMeshRef.current || !onModelTransform) return;
    const mesh = selectedMeshRef.current;
    onModelTransform(
      selectedModelId,
      mesh.position.toArray() as [number, number, number],
      [mesh.rotation.x, mesh.rotation.y, mesh.rotation.z],
      mesh.scale.toArray() as [number, number, number],
      { recordHistory: false, actionLabel: transformActionLabel },
    );
  }, [onModelTransform, selectedModelId, transformActionLabel]);

  // Deselect when clicking the bed/background
  const handlePointerMissed = useCallback(() => {
    onModelSelect?.(null);
    onSelectedModelMetricsChange?.(null);
  }, [onModelSelect, onSelectedModelMetricsChange]);

  useEffect(() => {
    if (!selectedModelId) {
      onSelectedModelMetricsChange?.(null);
    }
  }, [onSelectedModelMetricsChange, selectedModelId]);

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
                onSelectedMetrics={model.id === selectedModelId
                  ? (metrics) => onSelectedModelMetricsChange?.({
                    modelId: model.id,
                    baseSize: metrics.baseSize,
                    currentSize: metrics.currentSize,
                    currentScale: metrics.currentScale,
                  })
                  : undefined}
              />
            )}
          </Suspense>
        ))}
      </group>

      {/* TransformControls for the selected model */}
      {selectedModelId && transformMode && (
        <ModelTransformControls
          meshRef={selectedMeshRef}
          mode={transformMode}
          orbitRef={orbitRef}
          onTransform={handleTransformChange}
          onTransformStart={handleTransformStart}
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
  transformMode = null,
  onModelTransform,
  onSelectedModelMetricsChange,
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
            onSelectedModelMetricsChange={onSelectedModelMetricsChange}
            showAxes={showAxes}
          />
        </Suspense>
      </Canvas>
    </div>
  );
};

export default SlicerBedVisualization;
