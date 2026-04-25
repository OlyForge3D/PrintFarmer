import React, { Suspense, useRef, useState, useEffect, useCallback, MutableRefObject } from 'react';
// (renderUnknown not required here)
import { Canvas, useFrame, useLoader, useThree } from '@react-three/fiber';
import {
  OrbitControls,
  useProgress,
  Html,
  Environment
} from '@react-three/drei';
import { STLLoader } from 'three-stdlib';
import { PLYLoader } from 'three-stdlib';
import * as THREE from 'three';
import { TextureLoader } from 'three';
import { PerspectiveIcon, OrthographicIcon, RecenterIcon, RulerIcon, SimplifyIcon } from '../../../../common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui/Button';
import { MeasurementTool } from './MeasurementTool';
import { MeasurementOverlay } from './MeasurementOverlay';
import { DecimationPanel } from './DecimationPanel';
import { decimateGeometry, type DecimationResult } from '../../utils/meshDecimation';
import { exportSTL } from '../../utils/stlExporter';

export interface ModelViewerProps {
  modelUrl: string;
  fileType: 'stl' | '3mf' | 'obj' | 'ply' | 'step' | 'stp';
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

// Interface for model dimension tracking
interface ModelDimensions {
  width: number;  // X dimension in mm
  depth: number;  // Y dimension in mm
  height: number; // Z dimension in mm
  volume?: number; // Approximate volume in mm³
}

// View mode options
type ViewMode = 'solid' | 'wireframe' | 'xray';

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
  
  // Always call useLoader unconditionally (React hooks rules)
  const texture = useLoader(TextureLoader, textureUrl);
  
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

/**
 * Simple axis indicator lines at the origin (X=red, Y=green, Z=blue)
 */
function SimpleAxisIndicators({ size = 30 }: { size?: number }) {
  return (
    <group position={[0, 0, 0.1]}>
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute attach="attributes-position" args={[new Float32Array([0, 0, 0, size, 0, 0]), 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#ff0000" linewidth={3} />
      </line>
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute attach="attributes-position" args={[new Float32Array([0, 0, 0, 0, size, 0]), 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#00ff00" linewidth={3} />
      </line>
      <line>
        <bufferGeometry attach="geometry">
          <float32BufferAttribute attach="attributes-position" args={[new Float32Array([0, 0, 0, 0, 0, size]), 3]} />
        </bufferGeometry>
        <lineBasicMaterial color="#0088ff" linewidth={3} />
      </line>
    </group>
  );
}

/**
 * Mock print bed with line-based grid for the model viewer.
 * Since we don't know the target printer, the bed is sized based on the model
 * dimensions with padding, snapped to the nearest 10mm grid.
 */
function MockPrintBed({ modelDimensions }: { modelDimensions: ModelDimensions | null }) {
  const { cellLines, sectionLines, bedWidth, bedDepth } = React.useMemo(() => {
    // Minimum 300x300 bed, square, sized to fit model + padding snapped to 10mm
    const mw = modelDimensions?.width ?? 200;
    const md = modelDimensions?.depth ?? 200;
    const side = Math.ceil(Math.max(mw * 1.5, md * 1.5, 300) / 10) * 10;
    const w = side;
    const d = side;

    const halfW = w / 2;
    const halfD = d / 2;
    const cellSize = 10;
    const sectionSize = 50;
    const cellVerts: number[] = [];
    const sectionVerts: number[] = [];

    for (let x = -halfW; x <= halfW + 0.01; x += cellSize) {
      const rounded = Math.round(x / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(rounded, -halfD, 0, rounded, halfD, 0);
    }
    for (let y = -halfD; y <= halfD + 0.01; y += cellSize) {
      const rounded = Math.round(y / cellSize) * cellSize;
      const isSection = Math.abs(rounded % sectionSize) < 0.01;
      const target = isSection ? sectionVerts : cellVerts;
      target.push(-halfW, rounded, 0, halfW, rounded, 0);
    }

    return {
      cellLines: new Float32Array(cellVerts),
      sectionLines: new Float32Array(sectionVerts),
      bedWidth: w,
      bedDepth: d,
    };
  }, [modelDimensions]);

  const thickness = 1;

  return (
    <group>
      {/* Bed surface */}
      <mesh position={[0, 0, -thickness / 2]} receiveShadow userData={{ isBed: true }}>
        <boxGeometry args={[bedWidth, bedDepth, thickness]} />
        <meshStandardMaterial color="#1a1a2e" metalness={0.2} roughness={0.6} />
      </mesh>

      {/* Bed edge outline */}
      <lineSegments position={[0, 0, 0.01]}>
        <edgesGeometry attach="geometry">
          <planeGeometry args={[bedWidth, bedDepth]} />
        </edgesGeometry>
        <lineBasicMaterial color="#4a4a6a" linewidth={2} />
      </lineSegments>

      {/* Grid lines */}
      <group position={[0, 0, 0.05]}>
        <lineSegments>
          <bufferGeometry>
            <float32BufferAttribute attach="attributes-position" args={[cellLines, 3]} />
          </bufferGeometry>
          <lineBasicMaterial color="#555577" transparent opacity={0.4} />
        </lineSegments>
        <lineSegments>
          <bufferGeometry>
            <float32BufferAttribute attach="attributes-position" args={[sectionLines, 3]} />
          </bufferGeometry>
          <lineBasicMaterial color="#7777aa" transparent opacity={0.6} />
        </lineSegments>
      </group>
    </group>
  );
}

function STLModel({ url, color = "#0969da", viewMode = 'solid', onDimensionsChange, onGeometryLoaded }: { 
  url: string; 
  color?: string;
  viewMode?: ViewMode;
  onDimensionsChange?: (dimensions: ModelDimensions) => void;
  onGeometryLoaded?: (geometry: THREE.BufferGeometry) => void;
}) {
  const geometry = useLoader(STLLoader, url);
  const meshRef = useRef<THREE.Mesh>(null);

  // Transform geometry to proper orientation and position
  useEffect(() => {
    if (geometry) {
      // Calculate bounding box
      geometry.computeBoundingBox();
      if (!geometry.boundingBox) return;

      const bbox = geometry.boundingBox;
      const centerX = (bbox.min.x + bbox.max.x) / 2;
      const centerY = (bbox.min.y + bbox.max.y) / 2;
      const minZ = bbox.min.z;

      // Calculate dimensions in mm (assuming model is already in mm units)
      const width = bbox.max.x - bbox.min.x;
      const depth = bbox.max.y - bbox.min.y;
      const height = bbox.max.z - bbox.min.z;
      const volume = width * depth * height; // Approximate volume

      // Center the model in X and Y, place bottom at Z=0
      geometry.translate(-centerX, -centerY, -minZ);
      
      // Ensure normals are computed for proper lighting
      geometry.computeVertexNormals();

      // Report dimensions to parent component
      if (onDimensionsChange) {
        onDimensionsChange({ width, depth, height, volume });
      }

      if (onGeometryLoaded) {
        onGeometryLoaded(geometry.clone());
      }
    }
  }, [geometry, onDimensionsChange, onGeometryLoaded]);

  // Render material based on view mode
  const renderMaterial = () => {
    const baseProps = {
      color,
      metalness: 0.3,
      roughness: 0.4,
    };

    switch (viewMode) {
      case 'wireframe':
        return <meshStandardMaterial {...baseProps} wireframe={true} />;
      case 'xray':
        return (
          <meshStandardMaterial 
            {...baseProps}
            transparent={true}
            opacity={0.3}
            side={THREE.DoubleSide}
          />
        );
      default: // solid
        return <meshStandardMaterial {...baseProps} />;
    }
  };

  return (
    <mesh
      ref={meshRef}
      geometry={geometry}
      position={[0, 0, 0]}
      castShadow={viewMode === 'solid'}
      receiveShadow={viewMode === 'solid'}
    >
      {renderMaterial()}
    </mesh>
  );
}

/**
 * Camera fitter optimized for 3D printing models (typical size: 20-200mm)
 * This component should be placed inside Canvas to have access to useThree
 * 
 * Note: Three.js requires direct camera property mutations for near/far planes.
 * This is intentional and expected in the Three.js/R3F ecosystem.
 */
function CameraFitter() {
  "use no memo"; // Opt out of React Compiler - Three.js requires camera mutations
  const { camera, scene } = useThree();

  useEffect(() => {
    // Compute bounding box of model meshes only (exclude bed/grid helpers)
    const box = new THREE.Box3();

    scene.traverse((object: THREE.Object3D) => {
      if (object instanceof THREE.Mesh && object.geometry) {
        // Skip bed meshes (tagged with userData.isBed) and non-model geometry
        if (object.userData?.isBed) return;
        const geom = object.geometry as THREE.BufferGeometry;
        geom.computeBoundingBox();
        if (geom.boundingBox) {
          box.expandByObject(object);
        }
      }
    });

    // If we have a bounding box
    if (box.isEmpty()) {
      console.warn('[CameraFitter] Scene has no geometry');
      return;
    }

    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());

    // Calculate camera distance based on the full bounding box diagonal
    const boundingBoxDiagonal = size.length();
    
    let cameraDistance = 150;
    if ('fov' in camera && camera instanceof THREE.PerspectiveCamera) {
      const fov = camera.fov * (Math.PI / 180);
      cameraDistance = Math.abs(boundingBoxDiagonal / Math.tan(fov / 2));
      cameraDistance = Math.max(20, Math.min(2000, cameraDistance));
    }

    const paddingFactor = 1.5;
    cameraDistance *= paddingFactor;

    // Position camera for Z-up 3D printing view (isometric from front-right)
    const heightRatio = Math.max(0.5, size.z / boundingBoxDiagonal);
    const direction = new THREE.Vector3(1, -1, heightRatio).normalize();
    camera.position.copy(center).addScaledVector(direction, cameraDistance);
    camera.up.set(0, 0, 1);
    camera.lookAt(center);

    // Generous near/far clipping planes to prevent model clipping
    // eslint-disable-next-line react-hooks/immutability -- Three.js requires direct camera property mutations
    camera.near = 0.1;
    camera.far = cameraDistance * 20;
    camera.updateProjectionMatrix();
  }, [camera, scene]);

  return null;
}
function PLYModel({ url, color = "#0969da", viewMode = 'solid', onDimensionsChange, onGeometryLoaded }: { 
  url: string; 
  color?: string;
  viewMode?: ViewMode;
  onDimensionsChange?: (dimensions: ModelDimensions) => void;
  onGeometryLoaded?: (geometry: THREE.BufferGeometry) => void;
}) {
  const geometry = useLoader(PLYLoader, url);
  const meshRef = useRef<THREE.Mesh>(null);

  // Transform geometry to proper orientation and position
  useEffect(() => {
    if (geometry) {
      // Calculate bounding box
      geometry.computeBoundingBox();
      if (!geometry.boundingBox) return;

      const bbox = geometry.boundingBox;
      const centerX = (bbox.min.x + bbox.max.x) / 2;
      const centerY = (bbox.min.y + bbox.max.y) / 2;
      const minZ = bbox.min.z;

      // Calculate dimensions in mm (assuming model is already in mm units)
      const width = bbox.max.x - bbox.min.x;
      const depth = bbox.max.y - bbox.min.y;
      const height = bbox.max.z - bbox.min.z;
      const volume = width * depth * height; // Approximate volume

      // Center the model in X and Y, place bottom at Z=0
      geometry.translate(-centerX, -centerY, -minZ);
      
      // Ensure normals are computed for proper lighting
      geometry.computeVertexNormals();

      // Report dimensions to parent component
      if (onDimensionsChange) {
        onDimensionsChange({ width, depth, height, volume });
      }

      if (onGeometryLoaded) {
        onGeometryLoaded(geometry.clone());
      }
    }
  }, [geometry, onDimensionsChange, onGeometryLoaded]);

  // Render material based on view mode
  const renderMaterial = () => {
    const baseProps = {
      color,
      metalness: 0.3,
      roughness: 0.4,
      vertexColors: geometry.hasAttribute('color'),
    };

    switch (viewMode) {
      case 'wireframe':
        return <meshStandardMaterial {...baseProps} wireframe={true} />;
      case 'xray':
        return (
          <meshStandardMaterial 
            {...baseProps}
            transparent={true}
            opacity={0.3}
            side={THREE.DoubleSide}
          />
        );
      default: // solid
        return <meshStandardMaterial {...baseProps} />;
    }
  };

  return (
    <mesh
      ref={meshRef}
      geometry={geometry}
      position={[0, 0, 0]}
      castShadow={viewMode === 'solid'}
      receiveShadow={viewMode === 'solid'}
    >
      {renderMaterial()}
    </mesh>
  );
}

/**
 * Camera Controller - Handles smooth camera animations to different views and projection changes
 */
function CameraController({ 
  viewDirection,
  isPerspective
}: { 
  viewDirection: string | null;
  isPerspective: boolean;
}) {
  const { camera, gl } = useThree();

  useEffect(() => {
    if (!viewDirection) return;

    const distance = 100;
    // Z-up coordinate system: X=right, Y=back, Z=up
    const positions: Record<string, [number, number, number]> = {
      top: [0, 0, distance],        // Looking straight down at bed
      bottom: [0, 0, -distance],    // Looking up from below
      front: [0, -distance, 0],     // Looking at front edge of bed
      back: [0, distance, 0],       // Looking at back edge of bed
      left: [-distance, 0, 0],      // Looking from left side
      right: [distance, 0, 0],      // Looking from right side
      iso: [distance * 0.7, -distance * 0.7, distance * 0.5], // Isometric: slightly above
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

      camera.up.set(0, 0, 1); // Maintain Z-up during animation
      camera.lookAt(0, 0, 0);

      if (t < 1) {
        requestAnimationFrame(animate);
      }
    };

    requestAnimationFrame(animate);
  }, [viewDirection, camera]);

  // Handle perspective/orthographic switching
  useEffect(() => {
    const currentCamera = camera;
    
    if (isPerspective && !(currentCamera instanceof THREE.PerspectiveCamera)) {
      // Switch to perspective camera
      const perspectiveCamera = new THREE.PerspectiveCamera(
        45,
        gl.domElement.width / gl.domElement.height,
        0.1,
        1000
      );
      
      perspectiveCamera.position.copy(currentCamera.position);
      perspectiveCamera.lookAt(0, 0, 0);
      perspectiveCamera.up.set(0, 0, 1); // Z-up for 3D printing
      
      // This would require changing the camera reference which is complex in R3F
      if (window.PrintFarmerDebug?.models3d) {
        console.log('Perspective camera switch requested');
      }
    } else if (!isPerspective && !(currentCamera instanceof THREE.OrthographicCamera)) {
      // Switch to orthographic camera
      if (window.PrintFarmerDebug?.models3d) {
        console.log('Orthographic camera switch requested');
      }
    }
  }, [isPerspective, camera, gl]);

  return null;
}

// Camera-relative lighting that follows the viewpoint
function CameraFollowingLights() {
  const { camera } = useThree();
  const lightRef = useRef<THREE.DirectionalLight>(null);
  const light2Ref = useRef<THREE.DirectionalLight>(null);

  useFrame(() => {
    if (lightRef.current && light2Ref.current) {
      // Main light follows camera closely - like a headlight at eye level
      const cameraDirection = camera.position.clone().normalize();
      const lightOffset1 = cameraDirection.clone().multiplyScalar(20);
      lightOffset1.add(new THREE.Vector3(2, -1, 2)); // Minimal offset, mostly at eye level
      
      // Secondary light slightly to the side for fill
      const lightOffset2 = cameraDirection.clone().multiplyScalar(18);
      lightOffset2.add(new THREE.Vector3(-3, 1, 0)); // Side fill light at same level
      
      lightRef.current.position.copy(lightOffset1);
      light2Ref.current.position.copy(lightOffset2);
      
      // Make lights look at the origin (where model is)
      lightRef.current.lookAt(0, 0, 0);
      light2Ref.current.lookAt(0, 0, 0);
    }
  });

  return (
    <>
      {/* Main camera-following light */}
      <directionalLight
        ref={lightRef}
        intensity={0.8}
        castShadow
        shadow-mapSize={[1024, 1024]}
        shadow-camera-far={200}
        shadow-camera-left={-50}
        shadow-camera-right={50}
        shadow-camera-top={50}
        shadow-camera-bottom={-50}
      />
      
      {/* Secondary camera-following light for fill */}
      <directionalLight
        ref={light2Ref}
        intensity={0.4}
      />
    </>
  );
}

export const ModelViewer: React.FC<ModelViewerProps> = ({
  modelUrl,
  fileType,
  showGrid = true,
  showAxes = true,
  autoRotate = false,
  className = "h-160 w-full",
  bedDimensions,
  bedTextureUrl,
  bedTextureFormat
}) => {
  const [error, setError] = useState<string | null>(null);
  const [viewDirection, setViewDirection] = useState<string | null>(null);
  const [modelDimensions, setModelDimensions] = useState<ModelDimensions | null>(null);
  const [isPerspective, setIsPerspective] = useState(true);
  const [viewMode, setViewMode] = useState<ViewMode>('solid');
  const [isGridVisible, setIsGridVisible] = useState(showGrid);
  const [measurementActive, setMeasurementActive] = useState(false);
  const [lastDistance, setLastDistance] = useState<number | null>(null);
  const [measurementKey, setMeasurementKey] = useState(0);
  const [decimationActive, setDecimationActive] = useState(false);
  const [decimationResult, setDecimationResult] = useState<DecimationResult | null>(null);
  const [originalGeometry, setOriginalGeometry] = useState<THREE.BufferGeometry | null>(null);
  const [isDecimating, setIsDecimating] = useState(false);
  // orbitControlsRef holds the R3F OrbitControls instance. Use `any` here to avoid importing three internals.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const orbitControlsRef = useRef<any>(null) as MutableRefObject<any>;

  // Update grid visibility when prop changes
  useEffect(() => {
    setIsGridVisible(showGrid);
  }, [showGrid]);

  const handleRecenter = () => {
    setViewDirection('iso'); // Reset to isometric view
    setTimeout(() => setViewDirection(null), 100); // Clear after animation
  };

  const handleToggleProjection = () => {
    setIsPerspective(!isPerspective);
  };

  const handleViewModeChange = () => {
    setViewMode((current) => {
      switch (current) {
        case 'solid': return 'wireframe';
        case 'wireframe': return 'xray';
        case 'xray': return 'solid';
        default: return 'solid';
      }
    });
  };

  const handleToggleGrid = () => {
    setIsGridVisible(!isGridVisible);
  };

  const handleToggleMeasurement = () => {
    setMeasurementActive((prev) => {
      if (!prev) {
        setMeasurementKey((k) => k + 1);
      }
      setLastDistance(null);
      return !prev;
    });
  };

  const handleMeasurement = useCallback((distance: number | null) => {
    setLastDistance(distance);
  }, []);

  const handleClearMeasurement = useCallback(() => {
    setLastDistance(null);
  }, []);

  const handleGeometryLoaded = useCallback((geo: THREE.BufferGeometry) => {
    setOriginalGeometry(geo);
  }, []);

  // Reset decimation state when model changes
  useEffect(() => {
    setDecimationActive(false);
    setDecimationResult((prev) => {
      prev?.geometry.dispose();
      return null;
    });
  }, [modelUrl, fileType]);

  // Dispose GPU geometry on unmount
  useEffect(() => {
    return () => {
      decimationResult?.geometry.dispose();
      originalGeometry?.dispose();
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handleDecimationPreview = useCallback((reduction: number) => {
    if (!originalGeometry) return;
    setIsDecimating(true);
    requestAnimationFrame(() => {
      const result = decimateGeometry(originalGeometry, reduction);
      setDecimationResult((prev) => {
        prev?.geometry.dispose();
        return result;
      });
      setIsDecimating(false);
    });
  }, [originalGeometry]);

  const handleDecimationApply = useCallback(() => {
    if (!decimationResult) return;
    const filename = modelUrl.split('/').pop() ?? 'model';
    exportSTL(decimationResult.geometry, filename);
  }, [decimationResult, modelUrl]);

  const handleDecimationReset = useCallback(() => {
    setDecimationResult((prev) => {
      prev?.geometry.dispose();
      return null;
    });
  }, []);

  const handleToggleDecimation = useCallback(() => {
    setDecimationActive((prev) => {
      if (prev) {
        // Turning off: clear and dispose decimation result
        setDecimationResult((old) => {
          old?.geometry.dispose();
          return null;
        });
      }
      return !prev;
    });
  }, []);

  // Compute face/vertex counts from the original geometry for the panel
  const originalFaces = originalGeometry
    ? (originalGeometry.index
        ? originalGeometry.index.count / 3
        : originalGeometry.getAttribute('position').count / 3)
    : 0;
  const originalVertices = originalGeometry
    ? originalGeometry.getAttribute('position').count
    : 0;

  const renderModel = () => {
    // When decimation preview is active, render the decimated geometry directly
    if (decimationResult) {
      return (
        <mesh
          geometry={decimationResult.geometry}
          position={[0, 0, 0]}
          castShadow={viewMode === 'solid'}
          receiveShadow={viewMode === 'solid'}
        >
          <meshStandardMaterial
            color="#0969da"
            metalness={0.3}
            roughness={0.4}
            wireframe={viewMode === 'wireframe'}
            transparent={viewMode === 'xray'}
            opacity={viewMode === 'xray' ? 0.3 : 1}
            side={viewMode === 'xray' ? THREE.DoubleSide : THREE.FrontSide}
          />
        </mesh>
      );
    }

    switch (fileType) {
      case 'stl':
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} onGeometryLoaded={handleGeometryLoaded} />;
      case 'ply':
        return <PLYModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} onGeometryLoaded={handleGeometryLoaded} />;
      case '3mf':
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} onGeometryLoaded={handleGeometryLoaded} />;
      default:
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} onGeometryLoaded={handleGeometryLoaded} />;
    }
  };

  // STEP/STP files are supported for slicing but cannot be previewed in browser (Three.js has no STEP loader)
  if (fileType === 'step' || fileType === 'stp') {
    return (
      <div className={`${className} flex items-center justify-center bg-pf-bg-1 rounded-lg border border-pf-border`}>
        <div className="text-center">
          <p className="text-pf-text-secondary font-medium">Preview not available for STEP files</p>
          <p className="text-pf-text-tertiary text-sm mt-1">STEP files can still be sliced by OrcaSlicer and PrusaSlicer</p>
        </div>
      </div>
    );
  }

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
    <div className={`${className} border border-pf-border rounded-lg overflow-hidden relative`} style={{ backgroundColor: '#2d3748' }}>
      <Canvas
        camera={{ 
          position: [150, -150, 120], // Isometric view looking at the XY bed plane
          fov: 45,
          up: [0, 0, 1] // Z-up: standard 3D printing convention (Z = height)
        }}
        shadows
        style={{ backgroundColor: '#2d3748' }} // Darker gray background
        onCreated={({ gl }) => {
          gl.shadowMap.enabled = true;
          gl.shadowMap.type = THREE.PCFSoftShadowMap;
        }}
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
        {/* Camera-relative lighting that follows your viewing direction */}
        <ambientLight intensity={0.3} />
        <CameraFollowingLights />
        
        {/* Subtle fixed fill light to prevent complete darkness from some angles */}
        <pointLight position={[0, 0, 20]} intensity={0.2} />

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

        {/* Auto-fit camera to model bounds */}
        <CameraFitter />

        {/* Measurement tool (inside Canvas for raycasting) */}
        <MeasurementTool key={measurementKey} active={measurementActive} onMeasurement={handleMeasurement} />

        {/* Enhanced controls with full 360° rotation and zoom limits */}
        <OrbitControls
          ref={orbitControlsRef}
          enableDamping
          dampingFactor={0.05}
          autoRotate={autoRotate && !measurementActive}
          autoRotateSpeed={0.5}
          target={[0, 0, 0]}
          enablePan={true}
          enableZoom={true}
          minDistance={10}
          maxDistance={800}
        />

        {/* Camera Controller for view animations */}
        <CameraController 
          viewDirection={viewDirection} 
          isPerspective={isPerspective}
        />

        {/* Mock print bed with grid lines on XY plane */}
        {isGridVisible && (
          <MockPrintBed modelDimensions={modelDimensions} />
        )}

        {showAxes && <SimpleAxisIndicators size={modelDimensions ? Math.max(modelDimensions.width, modelDimensions.depth) * 0.2 : 30} />}
      </Canvas>

      {/* Measurement overlay (outside Canvas) */}
      <MeasurementOverlay
        active={measurementActive}
        distance={lastDistance}
        onClear={handleClearMeasurement}
        onDeactivate={handleToggleMeasurement}
      />

      {/* Camera Controls */}
      <div className="absolute top-4 right-4 flex gap-2">
        <Button
          onClick={handleToggleDecimation}
          variant="subtle"
          size="sm"
          className={`${decimationActive ? 'bg-pf-accent/20 border-pf-accent' : 'bg-pf-bg-2/95 border-pf-border'} backdrop-blur-sm hover:bg-pf-bg-2 rounded-lg p-2 transition-colors`}
          title={decimationActive ? "Close Simplifier" : "Simplify Mesh"}
          disabled={!originalGeometry}
        >
          <SimplifyIcon className="w-5 h-5 text-pf-text-primary" />
        </Button>

        <Button
          onClick={handleToggleMeasurement}
          variant="subtle"
          size="sm"
          className={`${measurementActive ? 'bg-pf-accent/20 border-pf-accent' : 'bg-pf-bg-2/95 border-pf-border'} backdrop-blur-sm hover:bg-pf-bg-2 rounded-lg p-2 transition-colors`}
          title={measurementActive ? "Disable Measurement" : "Measure Distance"}
        >
          <RulerIcon className="w-5 h-5 text-pf-text-primary" />
        </Button>

        <Button
          onClick={handleToggleGrid}
          variant="subtle"
          size="sm"
          className={`${isGridVisible ? 'bg-pf-accent/20 border-pf-accent' : 'bg-pf-bg-2/95 border-pf-border'} backdrop-blur-sm hover:bg-pf-bg-2 rounded-lg p-2 transition-colors`}
          title={isGridVisible ? "Hide Grid" : "Show Grid"}
        >
          <span className="text-xs font-medium text-pf-text-primary">📐</span>
        </Button>

        <Button
          onClick={handleViewModeChange}
          variant="subtle"
          size="sm"
          className="bg-pf-bg-2/95 backdrop-blur-sm hover:bg-pf-bg-2 border border-pf-border rounded-lg p-2 transition-colors"
          title={`Switch to ${viewMode === 'solid' ? 'Wireframe' : viewMode === 'wireframe' ? 'X-ray' : 'Solid'} View`}
        >
          <span className="text-xs font-medium text-pf-text-primary uppercase">
            {viewMode === 'solid' && '🎯'}
            {viewMode === 'wireframe' && '🔲'}
            {viewMode === 'xray' && '👻'}
          </span>
        </Button>

        <Button
          onClick={handleToggleProjection}
          variant="subtle"
          size="sm"
          className="bg-pf-bg-2/95 backdrop-blur-sm hover:bg-pf-bg-2 border border-pf-border rounded-lg p-2 transition-colors"
          title={isPerspective ? "Switch to Orthographic View" : "Switch to Perspective View"}
        >
          {isPerspective ? (
            <OrthographicIcon className="w-5 h-5 text-pf-text-primary" />
          ) : (
            <PerspectiveIcon className="w-5 h-5 text-pf-text-primary" />
          )}
        </Button>

        <Button
          onClick={handleRecenter}
          variant="subtle"
          size="sm"
          className="bg-pf-bg-2/95 backdrop-blur-sm hover:bg-pf-bg-2 border border-pf-border rounded-lg p-2 transition-colors"
          title="Recenter View"
        >
          <RecenterIcon className="w-5 h-5 text-pf-text-primary" />
        </Button>
      </div>

      {/* Decimation panel */}
      {decimationActive && originalGeometry && (
        <DecimationPanel
          originalFaces={originalFaces}
          originalVertices={originalVertices}
          onPreview={handleDecimationPreview}
          onApply={handleDecimationApply}
          onReset={handleDecimationReset}
          previewResult={decimationResult ? {
            resultFaces: decimationResult.resultFaces,
            resultVertices: decimationResult.resultVertices,
            reductionPercent: decimationResult.reductionPercent,
          } : undefined}
          isProcessing={isDecimating}
        />
      )}

      {/* Model dimensions badge — shift right when decimation panel is open */}
      {modelDimensions && !decimationActive && (
        <div className="absolute bottom-3 left-3 bg-pf-bg-2/90 backdrop-blur-sm px-2 py-1 rounded text-xs border border-pf-border text-pf-text-secondary">
          {modelDimensions.width.toFixed(1)} × {modelDimensions.depth.toFixed(1)} × {modelDimensions.height.toFixed(1)} mm
        </div>
      )}
    </div>
  );
};