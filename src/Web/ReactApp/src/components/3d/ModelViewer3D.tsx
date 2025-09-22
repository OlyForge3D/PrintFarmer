import React, { Suspense, useRef, useState } from 'react';
import { Canvas, useFrame, useLoader } from '@react-three/fiber';
import { 
  OrbitControls, 
  Grid, 
  Center, 
  useProgress, 
  Html,
  GizmoHelper,
  GizmoViewport,
  Environment
} from '@react-three/drei';
import { STLLoader } from 'three-stdlib';
import { PLYLoader } from 'three-stdlib';
import * as THREE from 'three';

export interface ModelViewerProps {
  modelUrl: string;
  fileType: 'stl' | '3mf' | 'obj' | 'ply';
  showGrid?: boolean;
  showAxes?: boolean;
  autoRotate?: boolean;
  className?: string;
}

function LoadingProgress() {
  const { progress } = useProgress();
  return (
    <Html center>
      <div className="bg-white px-4 py-2 rounded-lg shadow-lg">
        <div className="text-sm font-medium text-gray-900">
          Loading... {Math.round(progress)}%
        </div>
      </div>
    </Html>
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

  return (
    <Center>
      <mesh ref={meshRef} geometry={geometry} castShadow receiveShadow>
        <meshStandardMaterial 
          color={color} 
          metalness={0.3} 
          roughness={0.4} 
        />
      </mesh>
    </Center>
  );
}

function PLYModel({ url }: { url: string }) {
  const geometry = useLoader(PLYLoader, url);
  
  return (
    <Center>
      <mesh geometry={geometry} castShadow receiveShadow>
        <meshStandardMaterial vertexColors />
      </mesh>
    </Center>
  );
}

export const ModelViewer: React.FC<ModelViewerProps> = ({
  modelUrl,
  fileType,
  showGrid = true,
  showAxes = true,
  autoRotate = false,
  className = "h-96 w-full"
}) => {
  const [error, setError] = useState<string | null>(null);

  const renderModel = () => {
    switch (fileType) {
      case 'stl':
        return <STLModel url={modelUrl} />;
      case 'ply':
        return <PLYModel url={modelUrl} />;
      case '3mf':
        // 3MF requires server-side conversion to STL
        return <STLModel url={`/api/convert/3mf-to-stl?url=${encodeURIComponent(modelUrl)}`} />;
      default:
        return <STLModel url={modelUrl} />;
    }
  };

  if (error) {
    return (
      <div className={`${className} flex items-center justify-center bg-gray-100 rounded-lg border`}>
        <div className="text-center">
          <p className="text-red-600 font-medium">Failed to load 3D model</p>
          <p className="text-gray-500 text-sm mt-1">{error}</p>
        </div>
      </div>
    );
  }

  return (
    <div className={`${className} border rounded-lg overflow-hidden bg-gray-50 relative`}>
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
                return JSON.stringify(error);
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

      {/* Model info overlay */}
      <div className="absolute top-4 left-4 bg-white/90 backdrop-blur px-3 py-2 rounded-lg text-sm">
        <div className="font-medium">{fileType.toUpperCase()} Model</div>
        <div className="text-gray-600">Click and drag to rotate</div>
      </div>
    </div>
  );
};