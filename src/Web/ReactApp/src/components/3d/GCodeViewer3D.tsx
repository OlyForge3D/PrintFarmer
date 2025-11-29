import React, { useMemo, useState, useEffect } from 'react';
import { Canvas } from '@react-three/fiber';
import { Line, OrbitControls, Grid } from '@react-three/drei';
import * as THREE from 'three';
import { Button, Checkbox } from '@/components/ui';

interface GCodePoint {
  x: number;
  y: number;
  z: number;
  e?: number;
  f?: number;
  type: 'move' | 'extrude';
}

interface GCodeLayer {
  z: number;
  points: GCodePoint[];
  color: THREE.Color;
}

function parseGCode(gcode: string): GCodeLayer[] {
  const lines = gcode.split('\n');
  const layers: Map<number, GCodePoint[]> = new Map();
  let currentPos = { x: 0, y: 0, z: 0, e: 0 };
  
  lines.forEach(line => {
    const cleanLine = line.split(';')[0].trim();
    if (!cleanLine.startsWith('G1') && !cleanLine.startsWith('G0')) return;
    
    const x = cleanLine.match(/X([-\d.]+)/)?.[1];
    const y = cleanLine.match(/Y([-\d.]+)/)?.[1];
    const z = cleanLine.match(/Z([-\d.]+)/)?.[1];
    const e = cleanLine.match(/E([-\d.]+)/)?.[1];
    
    const newPos = {
      x: x ? parseFloat(x) : currentPos.x,
      y: y ? parseFloat(y) : currentPos.y,
      z: z ? parseFloat(z) : currentPos.z,
      e: e ? parseFloat(e) : currentPos.e,
    };
    
    const point: GCodePoint = {
      ...newPos,
      type: e && parseFloat(e) > currentPos.e ? 'extrude' : 'move'
    };
    
    const layerZ = Math.round(newPos.z * 100) / 100;
    if (!layers.has(layerZ)) {
      layers.set(layerZ, []);
    }
    layers.get(layerZ)!.push(point);
    
    currentPos = newPos;
  });
  
  // Convert to layers with colors
  return Array.from(layers.entries())
    .sort(([a], [b]) => a - b)
    .map(([z, points], index) => ({
      z,
      points,
      color: new THREE.Color().setHSL((index * 0.1) % 1, 0.8, 0.6)
    }));
}

function GCodePath({ layer, visible }: { layer: GCodeLayer; visible: boolean }) {
  const { points, extrudePoints } = useMemo(() => {
    const movePoints: THREE.Vector3[] = [];
    const extrudePoints: THREE.Vector3[] = [];
    
    layer.points.forEach(point => {
      const vec = new THREE.Vector3(point.x, point.y, point.z);
      if (point.type === 'extrude') {
        extrudePoints.push(vec);
      } else {
        movePoints.push(vec);
      }
    });
    
    return { points: movePoints, extrudePoints };
  }, [layer]);

  if (!visible) return null;

  return (
    <>
      {/* Extrusion lines (thick, colored) */}
      {extrudePoints.length > 1 && (
        <Line
          points={extrudePoints}
          color={layer.color}
          lineWidth={2}
        />
      )}
      
      {/* Travel moves (thin, gray) */}
      {points.length > 1 && (
        <Line
          points={points}
          color="#999999"
          lineWidth={0.5}
          dashed={true}
          dashSize={0.5}
          gapSize={0.5}
        />
      )}
    </>
  );
}

export interface GCodeViewerProps {
  gcodeUrl: string;
  className?: string;
}

export const GCodeViewer: React.FC<GCodeViewerProps> = ({ 
  gcodeUrl, 
  className = "h-96 w-full" 
}) => {
  const [gcode, setGCode] = useState<string>('');
  const [layers, setLayers] = useState<GCodeLayer[]>([]);
  const [currentLayer, setCurrentLayer] = useState<number>(0);
  const [playAnimation, setPlayAnimation] = useState(false);
  const [showMoves, setShowMoves] = useState(false);

  useEffect(() => {
    fetch(gcodeUrl)
      .then(res => res.text())
      .then(code => {
        setGCode(code);
        const parsedLayers = parseGCode(code);
        setLayers(parsedLayers);
        setCurrentLayer(parsedLayers.length - 1);
      })
      .catch(error => {
        console.error('Failed to load G-code:', error);
      });
  }, [gcodeUrl]);

  useEffect(() => {
    if (!playAnimation) return;
    
    const interval = setInterval(() => {
      setCurrentLayer(prev => {
        if (prev >= layers.length - 1) {
          setPlayAnimation(false);
          return prev;
        }
        return prev + 1;
      });
    }, 100);
    
    return () => clearInterval(interval);
  }, [playAnimation, layers.length]);

  const printStats = useMemo(() => {
    if (layers.length === 0) return null;
    
    const totalPoints = layers.reduce((sum, layer) => sum + layer.points.length, 0);
    const printVolume = {
      x: { min: Infinity, max: -Infinity },
      y: { min: Infinity, max: -Infinity },
      z: { min: Infinity, max: -Infinity }
    };
    
    layers.forEach(layer => {
      layer.points.forEach(point => {
        printVolume.x.min = Math.min(printVolume.x.min, point.x);
        printVolume.x.max = Math.max(printVolume.x.max, point.x);
        printVolume.y.min = Math.min(printVolume.y.min, point.y);
        printVolume.y.max = Math.max(printVolume.y.max, point.y);
        printVolume.z.min = Math.min(printVolume.z.min, point.z);
        printVolume.z.max = Math.max(printVolume.z.max, point.z);
      });
    });
    
    return {
      layers: layers.length,
      points: totalPoints,
      dimensions: {
        x: Math.round((printVolume.x.max - printVolume.x.min) * 10) / 10,
        y: Math.round((printVolume.y.max - printVolume.y.min) * 10) / 10,
        z: Math.round((printVolume.z.max - printVolume.z.min) * 10) / 10,
      }
    };
  }, [layers]);

  if (!gcode) {
    return (
      <div className={`${className} flex items-center justify-center`}>
        <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Controls */}
      <div className="bg-white rounded-lg shadow p-4">
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-medium text-lg">G-code Preview</h3>
          
          <div className="flex items-center space-x-3">
            <Checkbox
              id="show-moves"
              label="Show travel moves"
              checked={showMoves}
              onChange={(e) => setShowMoves(e.target.checked)}
              className="text-sm"
            />
            
            <Button
              variant={playAnimation ? 'danger' : 'primary'}
              size="sm"
              onClick={() => setPlayAnimation(!playAnimation)}
            >
              {playAnimation ? 'Pause' : 'Play'} Animation
            </Button>
            
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setCurrentLayer(layers.length - 1)}
            >
              Show All
            </Button>
          </div>
        </div>
        
        {/* Layer slider */}
        <div className="space-y-2">
          <div className="flex items-center justify-between text-sm">
            <span>Layer: {currentLayer + 1} / {layers.length}</span>
            <span>Z: {layers[currentLayer]?.z.toFixed(2)}mm</span>
          </div>
          
          <input
            type="range"
            min={0}
            max={layers.length - 1}
            value={currentLayer}
            onChange={(e) => {
              setCurrentLayer(parseInt(e.target.value));
              setPlayAnimation(false);
            }}
            className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer"
          />
        </div>

        {/* Print stats */}
        {printStats && (
          <div className="mt-4 grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
            <div>
              <span className="text-gray-600">Layers:</span>
              <div className="font-medium">{printStats.layers}</div>
            </div>
            <div>
              <span className="text-gray-600">Dimensions:</span>
              <div className="font-medium">
                {printStats.dimensions.x} × {printStats.dimensions.y} × {printStats.dimensions.z}mm
              </div>
            </div>
            <div>
              <span className="text-gray-600">Points:</span>
              <div className="font-medium">{printStats.points.toLocaleString()}</div>
            </div>
          </div>
        )}
      </div>

      {/* 3D Viewer */}
      <div className={`${className} border rounded-lg bg-gray-900`}>
        <Canvas camera={{ position: [100, 100, 100], fov: 45 }}>
          <ambientLight intensity={0.3} />
          <pointLight position={[10, 10, 10]} intensity={0.8} />
          
          {layers.slice(0, currentLayer + 1).map((layer, index) => (
            <GCodePath 
              key={index}
              layer={layer}
              visible={true}
            />
          ))}
          
          <OrbitControls />
          <Grid args={[200, 200]} />
        </Canvas>
      </div>
    </div>
  );
};