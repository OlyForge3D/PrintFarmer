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
import { PerspectiveIcon, OrthographicIcon, RecenterIcon } from '../../../../common/components/icons/MdiIcons';

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

function STLModel({ url, color = "#0066cc", viewMode = 'solid', onDimensionsChange }: { 
  url: string; 
  color?: string;
  viewMode?: ViewMode;
  onDimensionsChange?: (dimensions: ModelDimensions) => void;
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
    }
  }, [geometry, onDimensionsChange]);

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
 */
function CameraFitter() {
  const { camera, scene } = useThree();

  useEffect(() => {
    // Compute bounding box of all visible objects
    const box = new THREE.Box3();

    scene.traverse((object) => {
      if (object instanceof THREE.Mesh && object.geometry) {
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
    // This ensures the entire model is visible regardless of proportions
    const boundingBoxDiagonal = size.length(); // 3D diagonal of the bounding box
    
    // Handle both PerspectiveCamera and OrthographicCamera
    let cameraDistance = 150; // default optimized for ~100mm models
    if ('fov' in camera && camera instanceof THREE.PerspectiveCamera) {
      const fov = camera.fov * (Math.PI / 180); // convert vertical FOV to radians
      // Use the bounding box diagonal to ensure entire model fits
      cameraDistance = Math.abs(boundingBoxDiagonal / Math.tan(fov / 2));
      
      // Remove restrictive clamping to allow proper viewing of all model sizes
      cameraDistance = Math.max(20, Math.min(2000, cameraDistance));
    }

    // Add padding based on model size - more consistent padding
    const paddingFactor = 1.5; // Consistent 50% padding for all models
    cameraDistance *= paddingFactor;

    // Position camera with Z pointing up (isometric view from front-right-top)
    // Adjust camera height based on model height for better framing
    const heightOffset = Math.max(0.3, size.z / boundingBoxDiagonal); // Proportional height offset
    const direction = new THREE.Vector3(1, -1, heightOffset).normalize();
    camera.position.copy(center).addScaledVector(direction, cameraDistance);
    
    // Look at the geometric center of the model
    camera.lookAt(center);

    // Update near/far clipping planes to handle wider range of model sizes
    camera.near = Math.max(0.1, cameraDistance / 500);
    camera.far = cameraDistance * 10;
    camera.updateProjectionMatrix();
  }, [camera, scene]);

  return null;
}
function PLYModel({ url, color = "#0066cc", viewMode = 'solid', onDimensionsChange }: { 
  url: string; 
  color?: string;
  viewMode?: ViewMode;
  onDimensionsChange?: (dimensions: ModelDimensions) => void;
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
    }
  }, [geometry, onDimensionsChange]);

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
  isPerspective,
  onRecenter
}: { 
  viewDirection: string | null;
  isPerspective: boolean;
  onRecenter: () => void;
}) {
  const { camera, gl } = useThree();

  useEffect(() => {
    if (!viewDirection) return;

    const distance = 100;
    const positions: Record<string, [number, number, number]> = {
      top: [0, 0, distance],
      bottom: [0, 0, -distance],
      front: [0, -distance, 0],
      back: [0, distance, 0],
      left: [-distance, 0, 0],
      right: [distance, 0, 0],
      iso: [distance * 0.7, -distance * 0.7, distance * 0.7],
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
      perspectiveCamera.up.set(0, 0, 1);
      
      // This would require changing the camera reference which is complex in R3F
      console.log('Perspective camera switch requested');
    } else if (!isPerspective && !(currentCamera instanceof THREE.OrthographicCamera)) {
      // Switch to orthographic camera
      console.log('Orthographic camera switch requested');
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
  className = "h-[40rem] w-full",
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
  const orbitControlsRef = useRef<any>(null);

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

  const renderModel = () => {
    switch (fileType) {
      case 'stl':
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} />;
      case 'ply':
        return <PLYModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} />;
      case '3mf':
        // 3MF files will be converted to STL by backend service
        // Frontend treats them as STL after conversion
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} />;
      default:
        return <STLModel url={modelUrl} viewMode={viewMode} onDimensionsChange={setModelDimensions} />;
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
    <div className={`${className} border border-pf-border rounded-lg overflow-hidden relative`} style={{ backgroundColor: '#2d3748' }}>
      <Canvas
        camera={{ 
          position: [100, -100, 80], // Z-up isometric view from front-right-above
          fov: 45,
          up: [0, 0, 1] // Set Z as up axis for correct orientation
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

        {/* Enhanced controls with full 360° rotation and zoom limits */}
        <OrbitControls
          ref={orbitControlsRef}
          enableDamping
          dampingFactor={0.05}
          autoRotate={autoRotate}
          autoRotateSpeed={0.5}
          target={[0, 0, 0]} // Orbit around model center
          enablePan={true}
          enableZoom={true}
          minDistance={10}  // Prevent zooming too close
          maxDistance={800} // Prevent zooming too far (model disappearing)
        />

        {/* Camera Controller for view animations */}
        <CameraController 
          viewDirection={viewDirection} 
          isPerspective={isPerspective}
          onRecenter={handleRecenter}
        />

        {/* Grid properly positioned on XY plane (slightly below Z=0) */}
        {isGridVisible && (
          <Grid 
            infiniteGrid
            cellSize={5} // 5mm squares - standard for 3D printing
            cellThickness={0.5}
            cellColor="#cbd5e0" // Light gray for contrast with dark background
            sectionSize={50} // 50mm major grid lines  
            sectionThickness={2.5} // Bolder 50mm major grid lines
            sectionColor="#e2e8f0" // Lighter gray for major grid lines
            fadeDistance={1000} // Increased fade distance for better visibility
            fadeStrength={0.5}  // Reduced fade strength to keep grid more visible
            followCamera={false}
            position={[0, 0, -0.1]} // Position slightly below Z=0 to avoid z-fighting
            rotation={[Math.PI / 2, 0, 0]} // Rotate to lie flat on XY plane with normal facing up
          />
        )}

        {showAxes && (
          <GizmoHelper alignment="bottom-right" margin={[80, 80]}>
            <GizmoViewport
              axisColors={['#ff2060', '#20df80', '#2080ff']} // X=Red, Y=Green, Z=Blue
              labelColor="white"
              axisHeadScale={1.2}
            />
          </GizmoHelper>
        )}
      </Canvas>

      {/* Camera Controls */}
      <div className="absolute top-4 right-4 flex gap-2">
        <button
          onClick={handleToggleGrid}
          className={`${isGridVisible ? 'bg-pf-accent/20 border-pf-accent' : 'bg-pf-bg-2/95 border-pf-border'} backdrop-blur hover:bg-pf-bg-3 rounded-lg p-2 transition-colors`}
          title={isGridVisible ? "Hide Grid" : "Show Grid"}
        >
          <span className="text-xs font-medium text-pf-text-primary">
            📐
          </span>
        </button>
        <button
          onClick={handleViewModeChange}
          className="bg-pf-bg-2/95 backdrop-blur hover:bg-pf-bg-3 border border-pf-border rounded-lg p-2 transition-colors"
          title={`Switch to ${viewMode === 'solid' ? 'Wireframe' : viewMode === 'wireframe' ? 'X-ray' : 'Solid'} View`}
        >
          <span className="text-xs font-medium text-pf-text-primary uppercase">
            {viewMode === 'solid' && '🎯'}
            {viewMode === 'wireframe' && '🔲'} 
            {viewMode === 'xray' && '👻'}
          </span>
        </button>
        <button
          onClick={handleToggleProjection}
          className="bg-pf-bg-2/95 backdrop-blur hover:bg-pf-bg-3 border border-pf-border rounded-lg p-2 transition-colors"
          title={isPerspective ? "Switch to Orthographic View" : "Switch to Perspective View"}
        >
          {isPerspective ? (
            <OrthographicIcon className="w-5 h-5 text-pf-text-primary" />
          ) : (
            <PerspectiveIcon className="w-5 h-5 text-pf-text-primary" />
          )}
        </button>
        <button
          onClick={handleRecenter}
          className="bg-pf-bg-2/95 backdrop-blur hover:bg-pf-bg-3 border border-pf-border rounded-lg p-2 transition-colors"
          title="Recenter View"
        >
          <RecenterIcon className="w-5 h-5 text-pf-text-primary" />
        </button>
      </div>

      {/* Model Information Panel */}
      <div className="absolute top-4 left-4 bg-pf-bg-2/95 backdrop-blur px-3 py-2 rounded-lg text-sm border border-pf-border space-y-1">
        <div className="font-medium text-pf-text-primary">{fileType.toUpperCase()} Model</div>
        <div className="text-pf-text-secondary text-xs">
          Click and drag to rotate • Scroll to zoom • {isPerspective ? 'Perspective' : 'Orthographic'} • {viewMode.charAt(0).toUpperCase() + viewMode.slice(1)} view{isGridVisible ? ' • Grid on' : ''}
        </div>
        
        {/* Model dimensions display */}
        {modelDimensions && (
          <div className="text-xs text-pf-text-muted border-t border-pf-border/30 pt-1 mt-1">
            <div className="font-medium text-pf-text-secondary">Model Size:</div>
            <div>{modelDimensions.width.toFixed(1)} × {modelDimensions.depth.toFixed(1)} × {modelDimensions.height.toFixed(1)} mm</div>
            {modelDimensions.volume && (
              <div className="text-xs opacity-75">~{(modelDimensions.volume / 1000).toFixed(1)} cm³</div>
            )}
          </div>
        )}
        
        {/* Print bed dimensions */}
        {bedDimensions && (
          <div className="text-xs text-pf-text-muted border-t border-pf-border/30 pt-1">
            <div className="font-medium text-pf-text-secondary">Print Bed:</div>
            <div>{bedDimensions.width} × {bedDimensions.depth} mm</div>
            <div className="text-xs opacity-75">Grid: 5mm squares at Z=0</div>
          </div>
        )}
      </div>
    </div>
  );
};