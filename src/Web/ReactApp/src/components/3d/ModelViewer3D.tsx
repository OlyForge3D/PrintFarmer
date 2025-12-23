import React, { Suspense, useRef, useState, useEffect } from 'react';
// (renderUnknown not required here)
import { Canvas, useFrame, useLoader, useThree } from '@react-three/fiber';
import {
  OrbitControls,
  Grid,
  useProgress,
  Html,
  GizmoHelper,
  GizmoViewport,
  Environment
} from '@react-three/drei';
import { STLLoader } from 'three-stdlib';
import { PLYLoader } from 'three-stdlib';
import * as THREE from 'three';
import { TextureLoader } from 'three';
import { getApiBaseUrl } from '@/utils/apiUrlHelpers';

export interface ModelViewerProps {
  modelUrl: string;
  fileType: 'stl' | '3mf' | 'obj' | 'ply';
  showGrid?: boolean;
  showAxes?: boolean;
  autoRotate?: boolean;
  className?: string;
  bedDimensions?: {
    width: number;  // X axis (mm)
    depth: number;  // Y axis (mm)
    height?: number; // Z axis (mm) - optional for visualization
  };
  bedTextureUrl?: string;     // URL to SVG or PNG bed texture
  bedTextureFormat?: 'svg' | 'png';  // Format of bed texture
}

function LoadingProgress() {
  const { progress } = useProgress();
  return (
    <Html center>
      <div className="bg-pf-bg-2 px-4 py-2 rounded-lg shadow-lg border border-pf-border">
        <div className="text-sm font-medium text-pf-text-primary">
          Loading... {Math.round(progress)}%
        </div>
      </div>
    </Html>
  );
}

/**
 * Textured bed component - loads PNG texture for the bed surface
 */
function TexturedPrintBed({
  width,
  depth,
  height,
  textureUrl
}: {
  width: number;
  depth: number;
  height: number;
  textureUrl: string;
}) {
  const meshRef = useRef<THREE.Mesh>(null);
  
  let texture: THREE.Texture | null = null;
  try {
    // Use useLoader but catch errors gracefully
    texture = useLoader(TextureLoader, textureUrl);
  } catch (error) {
    // If texture fails to load, return plain bed instead
    console.warn(`[TexturedPrintBed] Failed to load texture from ${textureUrl}:`, error);
    return <PlainPrintBed width={width} depth={depth} height={height} />;
  }

  if (!texture) {
    return <PlainPrintBed width={width} depth={depth} height={height} />;
  }

  return (
    <>
      {/* Bed surface with texture */}
      <mesh
        ref={meshRef}
        position={[0, 0, -height / 2]}
        receiveShadow
      >
        <boxGeometry args={[width, depth, height]} />
        <meshStandardMaterial
          map={texture}
          metalness={0.1}
          roughness={0.5}
          transparent={false}
        />
      </mesh>

      {/* Bed outline */}
      <lineSegments>
        <edgesGeometry attach="geometry">
          <boxGeometry args={[width, depth, height]} />
        </edgesGeometry>
        <lineBasicMaterial color="#ffffff" linewidth={2} />
      </lineSegments>
    </>
  );
}

/**
 * Plain print bed (no texture)
 */
function PlainPrintBed({
  width,
  depth,
  height
}: {
  width: number;
  depth: number;
  height: number;
}) {
  const meshRef = useRef<THREE.Mesh>(null);

  return (
    <>
      {/* Bed surface - solid color */}
      <mesh
        ref={meshRef}
        position={[0, 0, -height / 2]}
        receiveShadow
      >
        <boxGeometry args={[width, depth, height]} />
        <meshStandardMaterial
          color="#1e40af"
          metalness={0.2}
          roughness={0.6}
          transparent={true}
          opacity={0.7}
        />
      </mesh>

      {/* Bed outline */}
      <lineSegments>
        <edgesGeometry attach="geometry">
          <boxGeometry args={[width, depth, height]} />
        </edgesGeometry>
        <lineBasicMaterial color="#ffffff" linewidth={2} />
      </lineSegments>
    </>
  );
}

/**
 * Print bed visualization showing the printer's build platform
 * Supports both solid color and textured bed surfaces using SVG or PNG textures
 * Objects are placed ON TOP of the bed (Z=0 plane)
 */
function PrintBed({
  width,
  depth,
  height = 0.5,
  textureUrl,
  textureFormat
}: {
  width: number;
  depth: number;
  height?: number;
  textureUrl?: string;
  textureFormat?: 'svg' | 'png';
}) {
  // Render appropriate bed component based on texture availability
  const shouldUsePngTexture = textureUrl && textureFormat === 'png';

  return (
    <group>
      {shouldUsePngTexture ? (
        <Suspense fallback={<PlainPrintBed width={width} depth={depth} height={height} />}>
          <TexturedPrintBed
            width={width}
            depth={depth}
            height={height}
            textureUrl={textureUrl}
          />
        </Suspense>
      ) : (
        <PlainPrintBed width={width} depth={depth} height={height} />
      )}

      {/* SVG texture overlay - render as image plane above the bed if provided */}
      {textureUrl && textureFormat === 'svg' && (
        <mesh position={[0, 0, (height ?? 0.5) / 2 + 0.01]}>
          <planeGeometry args={[width, depth]} />
          <meshBasicMaterial transparent={true} />
        </mesh>
      )}
    </group>
  );
}

function STLModel({ url, color = "#0066cc" }: { url: string; color?: string }) {
  const geometry = useLoader(STLLoader, url);
  const meshRef = useRef<THREE.Mesh>(null);

  useFrame((state) => {
    if (meshRef.current) {
      meshRef.current.rotation.y = Math.sin(state.clock.elapsedTime) * 0.1;
    }
  });

  // Compute bounding box and center the geometry
  // Position it on the bed (Z=0 is the top surface)
  // This ensures the model sits ON the bed, not with the bed through it
  const positionAttribute = geometry.getAttribute('position');
  if (positionAttribute && positionAttribute instanceof THREE.BufferAttribute) {
    const positions = (positionAttribute as THREE.BufferAttribute).array as Float32Array;

    // Find bounds
    let minX = Infinity, maxX = -Infinity;
    let minY = Infinity, maxY = -Infinity;
    let minZ = Infinity, maxZ = -Infinity;

    for (let i = 0; i < positions.length; i += 3) {
      minX = Math.min(minX, positions[i]);
      maxX = Math.max(maxX, positions[i]);
      minY = Math.min(minY, positions[i + 1]);
      maxY = Math.max(maxY, positions[i + 1]);
      minZ = Math.min(minZ, positions[i + 2]);
      maxZ = Math.max(maxZ, positions[i + 2]);
    }

    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;

    // Translate geometry so its bottom is at Z=0 and it's centered in X/Y
    geometry.translate(-centerX, -centerY, -minZ);
  }

  return (
    <mesh
      ref={meshRef}
      geometry={geometry}
      position={[0, 0, 0]}
      castShadow
      receiveShadow
    >
      <meshStandardMaterial
        color={color}
        metalness={0.3}
        roughness={0.4}
      />
    </mesh>
  );
}

function PLYModel({ url }: { url: string }) {
  const geometry = useLoader(PLYLoader, url);
  const meshRef = useRef<THREE.Mesh>(null);

  useFrame((state) => {
    if (meshRef.current) {
      meshRef.current.rotation.y = Math.sin(state.clock.elapsedTime) * 0.1;
    }
  });

  // Compute bounding box for PLY model same as STL
  const positionAttribute = geometry.getAttribute('position');
  if (positionAttribute && positionAttribute instanceof THREE.BufferAttribute) {
    const positions = (positionAttribute as THREE.BufferAttribute).array as Float32Array;

    // Find bounds
    let minX = Infinity, maxX = -Infinity;
    let minY = Infinity, maxY = -Infinity;
    let minZ = Infinity, maxZ = -Infinity;

    for (let i = 0; i < positions.length; i += 3) {
      minX = Math.min(minX, positions[i]);
      maxX = Math.max(maxX, positions[i]);
      minY = Math.min(minY, positions[i + 1]);
      maxY = Math.max(maxY, positions[i + 1]);
      minZ = Math.min(minZ, positions[i + 2]);
      maxZ = Math.max(maxZ, positions[i + 2]);
    }

    const centerX = (minX + maxX) / 2;
    const centerY = (minY + maxY) / 2;

    // Translate geometry so its bottom is at Z=0 and centered in X/Y
    geometry.translate(-centerX, -centerY, -minZ);
  }

  return (
    <mesh
      ref={meshRef}
      geometry={geometry}
      position={[0, 0, 0]}
      castShadow
      receiveShadow
    >
      <meshStandardMaterial vertexColors />
    </mesh>
  );
}

/**
 * Camera Controller - Handles smooth camera animations to different views
 */
function CameraController({ viewDirection }: { viewDirection: string | null }) {
  const { camera } = useThree();

  useEffect(() => {
    if (!viewDirection) return;

    const distance = 100;
    const positions: Record<string, [number, number, number]> = {
      top: [0, distance, 0],
      bottom: [0, -distance, 0],
      front: [0, 0, distance],
      back: [0, 0, -distance],
      left: [-distance, 0, 0],
      right: [distance, 0, 0],
      iso: [distance * 0.7, distance * 0.7, distance * 0.7],
    };

    const targetPosition = positions[viewDirection];
    if (!targetPosition) return;

    const startPos = { x: camera.position.x, y: camera.position.y, z: camera.position.z };
    const duration = 600; // 600ms animation
    const startTime = Date.now();

    const animate = () => {
      const elapsed = Date.now() - startTime;
      const t = Math.min(elapsed / duration, 1);

      // Smooth easing: cubic easeInOut
      const easeT = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;

      camera.position.x = startPos.x + (targetPosition[0] - startPos.x) * easeT;
      camera.position.y = startPos.y + (targetPosition[1] - startPos.y) * easeT;
      camera.position.z = startPos.z + (targetPosition[2] - startPos.z) * easeT;

      camera.lookAt(0, 0, 0);

      if (t < 1) {
        requestAnimationFrame(animate);
      }
    };

    requestAnimationFrame(animate);
  }, [viewDirection, camera]);

  return null;
}

export const ModelViewer: React.FC<ModelViewerProps> = ({
  modelUrl,
  fileType,
  showGrid = true,
  showAxes = true,
  autoRotate = false,
  className = "h-96 w-full",
  bedDimensions,
  bedTextureUrl,
  bedTextureFormat
}) => {
  const [error, setError] = useState<string | null>(null);
  const [viewDirection, setViewDirection] = useState<string | null>(null);

  const renderModel = () => {
    switch (fileType) {
      case 'stl':
        return <STLModel url={modelUrl} />;
      case 'ply':
        return <PLYModel url={modelUrl} />;
      case '3mf':
        // 3MF format is not currently supported for direct viewing
        // TODO: Implement 3MF to STL conversion service or use a library
        setError('3MF file format is not currently supported for viewing. Please convert to STL format.');
        return null;
      default:
        return <STLModel url={modelUrl} />;
    }
  };

  if (error) {
    return (
      <div className={`${className} flex items-center justify-center bg-pf-bg-1 rounded-lg border border-pf-border`}>
        <div className="text-center">
          <p className="text-pf-error font-medium">Failed to load 3D model</p>
          <p className="text-pf-text-secondary text-sm mt-1">{error}</p>
        </div>
      </div>
    );
  }

  return (
    <div className={`${className} border border-pf-border rounded-lg overflow-hidden bg-pf-bg-0 relative`}>
      <Canvas
        camera={{ position: [50, 50, 50], fov: 45 }}
        shadows
        onError={(error: unknown) => {
          const message = ((): string => {
            if (typeof error === 'string') return error;
            if (error && typeof error === 'object') {
              if ('message' in error) {
                const maybeMsg = (error as { message?: unknown }).message;
                if (typeof maybeMsg === 'string') return maybeMsg;
              }
              try {
                // Prefer message/stack if available to avoid serializing unknown objects
                if (error && typeof error === 'object') {
                  const maybeMsg = (error as { message?: unknown }).message;
                  if (typeof maybeMsg === 'string') return maybeMsg;
                }
              } catch { /* ignore */ }
            }
            return 'Failed to load model';
          })();
          setError(message);
        }}
      >
        {/* Lighting */}
        <ambientLight intensity={0.4} />
        <directionalLight
          position={[10, 10, 5]}
          intensity={1}
          castShadow
          shadow-mapSize-width={2048}
          shadow-mapSize-height={2048}
        />
        <pointLight position={[-10, -10, -10]} intensity={0.2} />

        {/* Environment */}
        <Environment preset="studio" />

        {/* Print bed - if printer dimensions provided */}
        {bedDimensions && (
          <PrintBed
            width={bedDimensions.width}
            depth={bedDimensions.depth}
            height={bedDimensions.height}
            textureUrl={bedTextureUrl}
            textureFormat={bedTextureFormat}
          />
        )}

        {/* 3D Model */}
        <Suspense fallback={<LoadingProgress />}>
          {renderModel()}
        </Suspense>

        {/* Controls and helpers */}
        <OrbitControls
          enableDamping
          dampingFactor={0.05}
          autoRotate={autoRotate}
          autoRotateSpeed={0.5}
        />

        {/* Camera Controller for view animations */}
        <CameraController viewDirection={viewDirection} />

        {showGrid && <Grid infiniteGrid />}

        {showAxes && (
          <GizmoHelper alignment="bottom-right" margin={[80, 80]}>
            <GizmoViewport
              axisColors={['#ff2060', '#20df80', '#2080ff']}
              labelColor="white"
            />
          </GizmoHelper>
        )}
      </Canvas>

      <div className="absolute top-4 left-4 bg-pf-bg-2/95 backdrop-blur px-3 py-2 rounded-lg text-sm border border-pf-border">
        <div className="font-medium text-pf-text-primary">{fileType.toUpperCase()} Model</div>
        <div className="text-pf-text-secondary">Click and drag to rotate</div>
        {bedDimensions && (
          <div className="text-xs text-pf-text-muted mt-1">
            Bed: {bedDimensions.width} × {bedDimensions.depth}mm
          </div>
        )}
      </div>
    </div>
  );
};