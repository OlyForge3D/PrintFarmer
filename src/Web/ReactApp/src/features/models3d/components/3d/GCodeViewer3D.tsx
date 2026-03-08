import React, { useMemo, useState, useEffect, useRef } from 'react';
import { Canvas } from '@react-three/fiber';
import { Line, OrbitControls, Grid } from '@react-three/drei';
import * as THREE from 'three';
import { Button, Checkbox, Select } from '@/common/components/ui';
import { GearIcon, SkipForwardIcon, PlayIcon, PauseIcon } from '@/common/components/icons/MdiIcons';

interface GCodePoint {
  x: number;
  y: number;
  z: number;
  e?: number;
  f?: number;
  type: 'move' | 'extrude';
  feedRate?: number;
}

interface GCodeLayer {
  z: number;
  points: GCodePoint[];
  color: THREE.Color;
}

interface ColorMode {
  id: number;
  label: string;
}

interface RenderQuality {
  value: number;
  label: string;
}

function parseGCode(gcode: string): GCodeLayer[] {
  const lines = gcode.split('\n');
  const layers: Map<number, GCodePoint[]> = new Map();
  let currentPos = { x: 0, y: 0, z: 0, e: 0, f: 0 };
  let minFeed = Infinity;
  let maxFeed = 0;
  
  // First pass: collect feed rates to normalize colors
  lines.forEach(line => {
    const cleanLine = line.split(';')[0].trim();
    if (!cleanLine.startsWith('G1') && !cleanLine.startsWith('G0')) return;
    
    const f = cleanLine.match(/F([\d.]+)/)?.[1];
    if (f) {
      const feedRate = parseFloat(f);
      minFeed = Math.min(minFeed, feedRate);
      maxFeed = Math.max(maxFeed, feedRate);
    }
  });
  
  if (minFeed === Infinity) minFeed = 20;
  if (maxFeed === 0) maxFeed = 100;
  
  // Second pass: parse points with normalized feed rates
  lines.forEach(line => {
    const cleanLine = line.split(';')[0].trim();
    if (!cleanLine.startsWith('G1') && !cleanLine.startsWith('G0')) return;
    
    const x = cleanLine.match(/X([-\d.]+)/)?.[1];
    const y = cleanLine.match(/Y([-\d.]+)/)?.[1];
    const z = cleanLine.match(/Z([-\d.]+)/)?.[1];
    const e = cleanLine.match(/E([-\d.]+)/)?.[1];
    const f = cleanLine.match(/F([\d.]+)/)?.[1];
    
    const newPos = {
      x: x ? parseFloat(x) : currentPos.x,
      y: y ? parseFloat(y) : currentPos.y,
      z: z ? parseFloat(z) : currentPos.z,
      e: e ? parseFloat(e) : currentPos.e,
      f: f ? parseFloat(f) : currentPos.f,
    };
    
    const point: GCodePoint = {
      ...newPos,
      type: e && parseFloat(e) > currentPos.e ? 'extrude' : 'move',
      feedRate: newPos.f,
    };
    
    const layerZ = Math.round(newPos.z * 100) / 100;
    if (!layers.has(layerZ)) {
      layers.set(layerZ, []);
    }
    layers.get(layerZ)!.push(point);
    
    currentPos = newPos;
  });
  
  // Convert to layers with colors based on feed rates
  return Array.from(layers.entries())
    .sort(([a], [b]) => a - b)
    .map(([z, points], index) => ({
      z,
      points,
      color: new THREE.Color().setHSL((index * 0.1) % 1, 0.8, 0.6),
    }));
}

function GCodePath({ layer, visible, colorMode, minFeedColor, maxFeedColor }: { 
  layer: GCodeLayer; 
  visible: boolean;
  colorMode: number;
  minFeedColor: string;
  maxFeedColor: string;
}) {
  const { extrudeSegments, moveSegments } = useMemo(() => {
    const extrudeSegments: Array<{ points: THREE.Vector3[]; color: THREE.Color }> = [];
    const moveSegments: Array<{ points: THREE.Vector3[]; color: THREE.Color }> = [];
    
    // Group consecutive points of same type
    let currentSegment: GCodePoint[] = [];
    let lastType: string = '';
    
    layer.points.forEach((point, idx) => {
      if (point.type !== lastType && currentSegment.length > 0) {
        const color = getPointColor(currentSegment, colorMode, minFeedColor, maxFeedColor);
        const vectors = currentSegment.map(p => new THREE.Vector3(p.x, p.y, p.z));
        
        if (lastType === 'extrude') {
          extrudeSegments.push({ points: vectors, color });
        } else {
          moveSegments.push({ points: vectors, color });
        }
        
        currentSegment = [];
        lastType = point.type;
      }
      
      currentSegment.push(point);
      
      if (idx === layer.points.length - 1) {
        const color = getPointColor(currentSegment, colorMode, minFeedColor, maxFeedColor);
        const vectors = currentSegment.map(p => new THREE.Vector3(p.x, p.y, p.z));
        
        if (point.type === 'extrude') {
          extrudeSegments.push({ points: vectors, color });
        } else {
          moveSegments.push({ points: vectors, color });
        }
      }
    });
    
    return { extrudeSegments, moveSegments };
  }, [layer, colorMode, minFeedColor, maxFeedColor]);

  if (!visible) return null;

  return (
    <>
      {/* Extrusion lines */}
      {extrudeSegments.map((segment, idx) => (
        segment.points.length > 1 && (
          <Line
            key={`extrude-${idx}`}
            points={segment.points}
            color={segment.color}
            lineWidth={2}
          />
        )
      ))}
      
      {/* Travel moves */}
      {moveSegments.map((segment, idx) => (
        segment.points.length > 1 && (
          <Line
            key={`move-${idx}`}
            points={segment.points}
            color={new THREE.Color('#666666')}
            lineWidth={0.5}
            dashed={true}
            dashSize={0.5}
            gapSize={0.5}
          />
        )
      ))}
    </>
  );
}

function getPointColor(points: GCodePoint[], colorMode: number, minFeedColor: string, maxFeedColor: string): THREE.Color {
  const color = new THREE.Color();
  
  if (colorMode === 0) {
    // Layer color (default hue)
    return color.setHSL(Math.random(), 0.8, 0.6);
  } else if (colorMode === 1) {
    // Speed-based color
    const avgFeed = points.reduce((sum, p) => sum + (p.feedRate || 0), 0) / points.length;
    const minFeed = 20;
    const maxFeed = 150;
    const normalized = Math.max(0, Math.min(1, (avgFeed - minFeed) / (maxFeed - minFeed)));
    
    const min = new THREE.Color(minFeedColor);
    const max = new THREE.Color(maxFeedColor);
    
    return color.lerpColors(min, max, normalized);
  } else if (colorMode === 2) {
    // Extrusion color
    const hasExtrusion = points.some(p => p.type === 'extrude');
    return color.set(hasExtrusion ? '#FF6B35' : '#666666');
  }
  
  return color;
}

export interface GCodeViewerProps {
  gcodeUrl: string;
  className?: string;
}

const COLOR_MODES: ColorMode[] = [
  { id: 0, label: 'Layer' },
  { id: 1, label: 'Speed' },
  { id: 2, label: 'Extrusion' },
];

const RENDER_QUALITIES: RenderQuality[] = [
  { value: 1, label: 'Low' },
  { value: 2, label: 'Medium' },
  { value: 3, label: 'High' },
  { value: 4, label: 'Ultra' },
];

export const GCodeViewer: React.FC<GCodeViewerProps> = ({ 
  gcodeUrl, 
  className = "h-96 w-full" 
}) => {
  const [gcode, setGCode] = useState<string>('');
  const [layers, setLayers] = useState<GCodeLayer[]>([]);
  const [currentLayer, setCurrentLayer] = useState<number>(0);
  const [playAnimation, setPlayAnimation] = useState(false);
  const [showMoves, setShowMoves] = useState(false);
  const [showGrid, setShowGrid] = useState(true);
  const [colorMode, setColorMode] = useState<number>(0);
  const [renderQuality, setRenderQuality] = useState<number>(2);
  const [backgroundColor, setBackgroundColor] = useState<string>('#1a1a1a');
  const [gridColor, setGridColor] = useState<string>('#4a4a4a');
  const [minFeedColor, setMinFeedColor] = useState<string>('#0000FF');
  const [maxFeedColor, setMaxFeedColor] = useState<string>('#FF0000');
  const [transparency, setTransparency] = useState(false);
  const [hdRendering, setHdRendering] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const settingsRef = useRef<HTMLDivElement>(null);

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
        <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Header with Controls */}
      <div className="bg-linear-to-r from-pf-bg-0 to-pf-bg-0 rounded-lg shadow-sm p-4 border border-pf-border">
        <div className="flex items-center justify-between mb-4">
          <div>
            <h3 className="font-bold text-lg text-white">G-Code Viewer</h3>
            {printStats && (
              <p className="text-xs text-pf-text-tertiary mt-1">
                {printStats.layers} layers • {printStats.dimensions.x}×{printStats.dimensions.y}×{printStats.dimensions.z}mm
              </p>
            )}
          </div>
          
          <div className="flex items-center space-x-2">
            <div className="relative">
              <Button
                type="button"
                variant="subtle"
                size="sm"
                onClick={() => setShowSettings(!showSettings)}
                className="p-2 rounded-md text-pf-text-secondary hover:text-white bg-transparent border-none shadow-none focus:ring-0"
                title="Settings"
              >
                <GearIcon className="w-5 h-5" />
              </Button>
              
              {/* Settings Dropdown */}
              {showSettings && (
                <div 
                  ref={settingsRef}
                  className="absolute right-0 top-full mt-2 w-72 bg-pf-bg-1 rounded-lg shadow-xl border border-pf-border p-4 z-50 space-y-3"
                >
                  <div className="border-b border-pf-border pb-3">
                    <h4 className="text-sm font-semibold text-white mb-2">Rendering</h4>
                    <div className="space-y-2 text-sm">
                      <div>
                        <label className="text-pf-text-secondary">Quality Level</label>
                        <Select
                          value={renderQuality}
                          onChange={(e) => setRenderQuality(parseInt(e.target.value))}
                          className="w-full bg-pf-bg-1 text-white rounded-sm px-2 py-1 text-xs border-pf-border"
                        >
                          {RENDER_QUALITIES.map(q => (
                            <option key={q.value} value={q.value}>{q.label}</option>
                          ))}
                        </Select>
                      </div>
                      <Checkbox
                        checked={hdRendering}
                        onChange={(e) => setHdRendering(e.currentTarget.checked)}
                        label="HD Rendering"
                        className="w-4 h-4"
                      />
                    </div>
                  </div>
                  
                  <div className="border-b border-pf-border pb-3">
                    <h4 className="text-sm font-semibold text-white mb-2">Display</h4>
                    <div className="space-y-2 text-sm">
                      <div>
                        <label className="text-pf-text-secondary">Color Mode</label>
                        <Select
                          value={colorMode}
                          onChange={(e) => setColorMode(parseInt(e.target.value))}
                          className="w-full bg-pf-bg-1 text-white rounded-sm px-2 py-1 text-xs border-pf-border"
                        >
                          {COLOR_MODES.map(m => (
                            <option key={m.id} value={m.id}>{m.label}</option>
                          ))}
                        </Select>
                      </div>
                      <Checkbox
                        checked={showGrid}
                        onChange={(e) => setShowGrid(e.currentTarget.checked)}
                        label="Show Grid"
                        className="w-4 h-4"
                      />
                      <Checkbox
                        checked={transparency}
                        onChange={(e) => setTransparency(e.currentTarget.checked)}
                        label="Transparency"
                        className="w-4 h-4"
                      />
                    </div>
                  </div>
                  
                  <div className="pb-3">
                    <h4 className="text-sm font-semibold text-white mb-2">Colors</h4>
                    <div className="space-y-2 text-sm">
                      <div className="flex items-center space-x-2">
                        <label className="text-pf-text-secondary flex-1">Background</label>
                        <input 
                          type="color" 
                          value={backgroundColor}
                          onChange={(e) => setBackgroundColor(e.target.value)}
                          className="w-8 h-8 rounded-sm cursor-pointer"
                        />
                      </div>
                      <div className="flex items-center space-x-2">
                        <label className="text-pf-text-secondary flex-1">Grid</label>
                        <input 
                          type="color" 
                          value={gridColor}
                          onChange={(e) => setGridColor(e.target.value)}
                          className="w-8 h-8 rounded-sm cursor-pointer"
                        />
                      </div>
                      {colorMode === 1 && (
                        <>
                          <div className="flex items-center space-x-2">
                            <label className="text-pf-text-secondary flex-1">Min Speed</label>
                            <input 
                              type="color" 
                              value={minFeedColor}
                              onChange={(e) => setMinFeedColor(e.target.value)}
                              className="w-8 h-8 rounded-sm cursor-pointer"
                            />
                          </div>
                          <div className="flex items-center space-x-2">
                            <label className="text-pf-text-secondary flex-1">Max Speed</label>
                            <input 
                              type="color" 
                              value={maxFeedColor}
                              onChange={(e) => setMaxFeedColor(e.target.value)}
                              className="w-8 h-8 rounded-sm cursor-pointer"
                            />
                          </div>
                        </>
                      )}
                    </div>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
        
        {/* Main Controls */}
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-3">
            <Button
              variant={playAnimation ? 'danger' : 'primary'}
              size="sm"
              onClick={() => setPlayAnimation(!playAnimation)}
              iconLeft={playAnimation ? <PauseIcon className="h-4 w-4" /> : <PlayIcon className="h-4 w-4" />}
            >
              {playAnimation ? 'Pause' : 'Play'}
            </Button>
            
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setCurrentLayer(layers.length - 1)}
              iconLeft={<SkipForwardIcon className="h-4 w-4" />}
            >
              Show All
            </Button>
          </div>
          
          <Checkbox
            id="show-moves"
            label="Travel Moves"
            checked={showMoves}
            onChange={(e) => setShowMoves(e.target.checked)}
            className="text-sm text-pf-text-secondary"
          />
        </div>
      </div>
      
      {/* Layer Slider */}
      <div className="bg-pf-bg-1 rounded-lg shadow-sm p-4 border border-pf-border">
        <div className="flex items-center justify-between text-sm text-pf-text-secondary mb-2">
          <span>Layer {currentLayer + 1} / {layers.length}</span>
          <span>Z: {layers[currentLayer]?.z.toFixed(2)}mm</span>
        </div>
        
        <input
          type="range"
          min={0}
          max={Math.max(0, layers.length - 1)}
          value={currentLayer}
          onChange={(e) => {
            setCurrentLayer(parseInt(e.target.value));
            setPlayAnimation(false);
          }}
          className="w-full h-2 bg-pf-bg-1 rounded-lg appearance-none cursor-pointer accent-pf-accent"
        />
      </div>

      {/* 3D Viewer */}
      <div className={`${className} border border-pf-border rounded-lg overflow-hidden`}>
        <Canvas 
          camera={{ position: [100, 100, 100], fov: 45 }}
          style={{ background: backgroundColor }}
        >
          <ambientLight intensity={0.5} />
          <pointLight position={[10, 10, 10]} intensity={1} />
          <pointLight position={[-10, -10, -10]} intensity={0.5} />
          
          {showGrid && <Grid args={[200, 200]} cellColor={gridColor} sectionColor={gridColor} />}
          
          {layers.slice(0, currentLayer + 1).map((layer, index) => (
            <GCodePath 
              key={index}
              layer={layer}
              visible={true}
              colorMode={colorMode}
              minFeedColor={minFeedColor}
              maxFeedColor={maxFeedColor}
            />
          ))}
          
          <OrbitControls />
        </Canvas>
      </div>
    </div>
  );
};