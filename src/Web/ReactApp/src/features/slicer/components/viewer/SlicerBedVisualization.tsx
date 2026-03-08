/**
 * Slicer 3D Bed Visualization Component
 * A Three.js canvas showing the print bed similar to OrcaSlicer
 */
import React, { Suspense, useRef, useEffect } from 'react';
import { Canvas, useThree, useLoader } from '@react-three/fiber';
import { OrbitControls, Grid, Environment, Html } from '@react-three/drei';
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

      {/* Grid lines on bed surface */}
      <Grid
        position={[0, 0, 0.02]}
        args={[width, depth]}
        cellSize={10}
        cellThickness={0.5}
        cellColor="#2a2a4a"
        sectionSize={50}
        sectionThickness={1}
        sectionColor="#3a3a5a"
        fadeDistance={1000}
        fadeStrength={0}
        infiniteGrid={false}
      />
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
  onClick
}: { 
  url: string;
  position?: [number, number, number];
  rotation?: [number, number, number];
  scale?: [number, number, number];
  selected?: boolean;
  onClick?: () => void;
}) {
  const geometry = useLoader(STLLoader, url);
  const meshRef = useRef<THREE.Mesh>(null);

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
      ref={meshRef}
      geometry={geometry}
      position={position}
      rotation={rotation}
      scale={scale}
      onClick={onClick}
      castShadow
      receiveShadow
    >
      <meshStandardMaterial 
        color={selected ? "#ff8844" : "#0088cc"}
        metalness={0.3}
        roughness={0.4}
        emissive={selected ? "#442200" : "#000000"}
      />
    </mesh>
  );
}

/**
 * Camera controller with optimal positioning
 */
function CameraController({ bedWidth, bedDepth, bedHeight }: { bedWidth: number; bedDepth: number; bedHeight: number }) {
  const { camera } = useThree();

  useEffect(() => {
    // Calculate camera distance based on bed size
    const maxDimension = Math.max(bedWidth, bedDepth, bedHeight);
    const distance = maxDimension * 1.5;

    // Position camera at isometric-ish angle
    camera.position.set(distance * 0.7, -distance * 0.7, distance * 0.6);
    camera.lookAt(0, 0, bedHeight / 4);
    camera.updateProjectionMatrix();
  }, [camera, bedWidth, bedDepth, bedHeight]);

  return (
    <OrbitControls
      enablePan={true}
      enableRotate={true}
      enableZoom={true}
      minDistance={50}
      maxDistance={2000}
      dampingFactor={0.05}
      rotateSpeed={0.8}
      zoomSpeed={1.2}
      minPolarAngle={0}
      maxPolarAngle={Math.PI / 2 + 0.3} // Allow slightly below horizon
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
  showAxes = true,
}: Omit<SlicerBedVisualizationProps, 'className' | 'backgroundColor' | 'showGrid' | 'gridDivisions'>) {
  const { width, depth, height, textureUrl, textureFormat } = bedConfig;

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
      <CameraController bedWidth={width} bedDepth={depth} bedHeight={height} />

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
            />
          )}
        </Suspense>
      ))}

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
  showAxes = true,
  backgroundColor = '#0f0f14',
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
          position: [300, -300, 250]
        }}
        onCreated={({ gl }) => {
          gl.toneMapping = THREE.ACESFilmicToneMapping;
          gl.toneMappingExposure = 1;
        }}
      >
        <Suspense fallback={null}>
          <BedScene
            bedConfig={bedConfig}
            models={models}
            selectedModelId={selectedModelId}
            onModelSelect={onModelSelect}
            showAxes={showAxes}
          />
        </Suspense>
      </Canvas>
    </div>
  );
};

export default SlicerBedVisualization;
