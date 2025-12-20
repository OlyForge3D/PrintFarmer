import React, { useEffect, useRef, useState } from 'react';
import { Canvas, useFrame } from '@react-three/fiber';
import { OrbitControls, Grid } from '@react-three/drei';
import * as THREE from 'three';

interface STLViewerProps {
  file?: File | ArrayBuffer | string;
  autoRotate?: boolean;
  cameraPosition?: [number, number, number];
  onMeshLoaded?: (mesh: THREE.Group) => void;
}

/**
 * STL Mesh Loader - Parses binary STL files
 * Handles both ASCII and binary STL formats
 */
function parseSTL(arrayBuffer: ArrayBuffer): THREE.BufferGeometry {
  const view = new DataView(arrayBuffer);
  const isASCII = isASCIISTL(arrayBuffer);

  if (isASCII) {
    return parseASCIISTL(arrayBuffer);
  } else {
    return parseBinarySTL(arrayBuffer);
  }
}

function isASCIISTL(arrayBuffer: ArrayBuffer): boolean {
  const view = new Uint8Array(arrayBuffer);
  const header = new TextDecoder().decode(view.slice(0, 5));
  return header === 'solid';
}

function parseBinarySTL(arrayBuffer: ArrayBuffer): THREE.BufferGeometry {
  const view = new DataView(arrayBuffer);
  const faces = view.getUint32(80, true);

  const geometry = new THREE.BufferGeometry();
  const vertices: number[] = [];
  const normals: number[] = [];

  let offset = 84;
  for (let i = 0; i < faces; i++) {
    // Read normal
    const nx = view.getFloat32(offset, true);
    const ny = view.getFloat32(offset + 4, true);
    const nz = view.getFloat32(offset + 8, true);
    offset += 12;

    // Read vertices
    for (let j = 0; j < 3; j++) {
      vertices.push(
        view.getFloat32(offset, true),
        view.getFloat32(offset + 4, true),
        view.getFloat32(offset + 8, true)
      );
      normals.push(nx, ny, nz);
      offset += 12;
    }

    offset += 2; // attribute byte count
  }

  geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vertices), 3));
  geometry.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(normals), 3));
  geometry.computeBoundingBox();

  return geometry;
}

function parseASCIISTL(arrayBuffer: ArrayBuffer): THREE.BufferGeometry {
  const text = new TextDecoder().decode(arrayBuffer);
  const geometry = new THREE.BufferGeometry();
  const vertices: number[] = [];
  const normals: number[] = [];

  const vertexPattern = /vertex\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)/g;
  const normalPattern = /facet\s+normal\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)\s+([-+]?[0-9]*\.?[0-9]+([eE][-+]?[0-9]+)?)/g;

  let vertexMatch;
  let normalMatch;

  while ((normalMatch = normalPattern.exec(text)) !== null) {
    const nx = parseFloat(normalMatch[1]);
    const ny = parseFloat(normalMatch[3]);
    const nz = parseFloat(normalMatch[5]);

    // Extract 3 vertices for this face
    for (let i = 0; i < 3; i++) {
      vertexMatch = vertexPattern.exec(text);
      if (vertexMatch) {
        vertices.push(
          parseFloat(vertexMatch[1]),
          parseFloat(vertexMatch[3]),
          parseFloat(vertexMatch[5])
        );
        normals.push(nx, ny, nz);
      }
    }
  }

  geometry.setAttribute('position', new THREE.BufferAttribute(new Float32Array(vertices), 3));
  geometry.setAttribute('normal', new THREE.BufferAttribute(new Float32Array(normals), 3));
  geometry.computeBoundingBox();

  return geometry;
}

/**
 * STL Model Component - Displays and handles 3D STL model
 */
interface STLModelProps {
  geometry: THREE.BufferGeometry;
  autoRotate?: boolean;
  onMeshLoaded?: (mesh: THREE.Group) => void;
}

function STLModel({ geometry, autoRotate = false, onMeshLoaded }: STLModelProps) {
  const meshRef = useRef<THREE.Group>(null);
  const materialRef = useRef<THREE.MeshPhongMaterial>(null);

  useEffect(() => {
    if (geometry) {
      geometry.center();
      geometry.computeBoundingBox();
      if (onMeshLoaded && meshRef.current) {
        onMeshLoaded(meshRef.current);
      }
    }
  }, [geometry, onMeshLoaded]);

  useFrame(() => {
    if (autoRotate && meshRef.current) {
      meshRef.current.rotation.x += 0.001;
      meshRef.current.rotation.y += 0.002;
    }
  });

  return (
    <group ref={meshRef}>
      <mesh geometry={geometry}>
        <meshPhongMaterial
          ref={materialRef}
          color={0x0066ff}
          specular={0x111111}
          shininess={200}
        />
      </mesh>
    </group>
  );
}

/**
 * STL Viewer Component
 * Displays STL files with interactive 3D controls
 */
export const STLViewer: React.FC<STLViewerProps> = ({
  file,
  autoRotate = false,
  cameraPosition = [0, 0, 100],
  onMeshLoaded,
}) => {
  const [geometry, setGeometry] = useState<THREE.BufferGeometry | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!file) {
      setLoading(false);
      return;
    }

    setLoading(true);
    setError(null);

    const loadSTL = async () => {
      try {
        let arrayBuffer: ArrayBuffer;

        if (file instanceof File) {
          arrayBuffer = await file.arrayBuffer();
        } else if (typeof file === 'string') {
          // File is a URL - fetch it
          const response = await fetch(file);
          if (!response.ok) {
            throw new Error(`Failed to fetch STL file: ${response.statusText}`);
          }
          arrayBuffer = await response.arrayBuffer();
        } else {
          arrayBuffer = file;
        }

        const parsedGeometry = parseSTL(arrayBuffer);
        setGeometry(parsedGeometry);
      } catch (err) {
        setError(`Failed to load STL file: ${err instanceof Error ? err.message : String(err)}`);
        console.error('STL loading error:', err);
      } finally {
        setLoading(false);
      }
    };

    loadSTL();
  }, [file]);

  if (loading) {
    return (
      <div className="w-full h-full flex items-center justify-center rounded-lg" style={{
        background: 'linear-gradient(to bottom, var(--pf-bg-0), var(--pf-bg-1))',
      }}>
        <div style={{ textAlign: 'center' }}>
          <div style={{ display: 'inline-block' }}>
            <div className="animate-spin rounded-full h-12 w-12 border-b-2" style={{ borderColor: 'var(--pf-accent-2)' }}></div>
          </div>
          <p className="mt-4" style={{ color: 'var(--pf-text-secondary)' }}>Loading STL model...</p>
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="w-full h-full flex items-center justify-center rounded-lg" style={{
        backgroundColor: 'var(--pf-error-bg)',
      }}>
        <div style={{
          textAlign: 'center',
          color: 'var(--pf-error)',
          padding: '1rem',
        }}>
          <p className="font-semibold">Error Loading Model</p>
          <p className="text-sm mt-2">{error}</p>
        </div>
      </div>
    );
  }

  if (!geometry) {
    return (
      <div className="w-full h-full flex items-center justify-center rounded-lg" style={{
        backgroundColor: 'var(--pf-bg-2)',
      }}>
        <p style={{ color: 'var(--pf-text-secondary)' }}>No model loaded</p>
      </div>
    );
  }

  return (
    <Canvas
      camera={{ position: cameraPosition, fov: 50 }}
      style={{ width: '100%', height: '100%' }}
      gl={{ antialias: true, alpha: true }}
    >
      {/* Lighting */}
      <ambientLight intensity={0.5} />
      <directionalLight position={[100, 100, 100]} intensity={0.8} />
      <directionalLight position={[-100, -100, -100]} intensity={0.3} />
      <pointLight position={[50, 50, 50]} intensity={0.4} />

      {/* Model */}
      <STLModel geometry={geometry} autoRotate={autoRotate} onMeshLoaded={onMeshLoaded} />

      {/* Grid */}
      <Grid args={[1000, 1000]} cellSize={10} cellColor="#6f7280" sectionSize={100} fadeDistance={500} fadeStrength={1} infiniteGrid />

      {/* Controls */}
      <OrbitControls
        makeDefault
        autoRotate={false}
        autoRotateSpeed={4}
        enableDamping
        dampingFactor={0.05}
        enableZoom
      />
    </Canvas>
  );
};

export default STLViewer;
