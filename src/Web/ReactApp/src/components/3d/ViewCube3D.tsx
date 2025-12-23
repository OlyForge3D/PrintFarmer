import React, { useState, useRef } from 'react';

interface ViewCube3DProps {
  onViewChange: (direction: 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right' | 'iso') => void;
  onDragRotation?: (deltaX: number, deltaY: number) => void;
}

/**
 * 2D View Cube Navigation Control with Drag Rotation
 * An interactive cube overlay positioned in the top-right corner of the 3D viewer.
 * Users can:
 * - Click faces to snap to that view
 * - Drag on the cube to rotate the 3D model in real-time
 * NOT rendered in the 3D scene - this is a pure 2D UI control.
 */
export const ViewCube3D: React.FC<ViewCube3DProps> = ({ onViewChange, onDragRotation }) => {
  const [hoveredFace, setHoveredFace] = useState<string | null>(null);
  const [isDragging, setIsDragging] = useState(false);
  const [rotation, setRotation] = useState({ x: -20, z: 30 });
  const dragStartRef = useRef({ x: 0, y: 0 });
  const containerRef = useRef<HTMLDivElement>(null);

  const handleFaceClick = (e: React.MouseEvent, face: string) => {
    // Only trigger snap if it's a direct click (not a drag)
    if (!isDragging) {
      e.stopPropagation();
      onViewChange(face as 'top' | 'bottom' | 'front' | 'back' | 'left' | 'right' | 'iso');
    }
  };

  const handleMouseDown = (e: React.MouseEvent) => {
    setIsDragging(true);
    dragStartRef.current = { x: e.clientX, y: e.clientY };
  };

  const handleMouseMove = (e: React.MouseEvent) => {
    if (!isDragging) return;

    const deltaX = e.clientX - dragStartRef.current.x;
    const deltaY = e.clientY - dragStartRef.current.y;

    // Update rotation based on mouse movement (negated for intuitive direction)
    setRotation(prev => ({
      x: prev.x - deltaY * 0.5, // Moving down rotates up
      z: prev.z - deltaX * 0.5  // Moving left rotates right
    }));

    dragStartRef.current = { x: e.clientX, y: e.clientY };
    
    // Send drag deltas to parent to update the 3D model camera
    // Use the raw pixel deltas - parent will apply rotation sensitivity
    if (onDragRotation) {
      onDragRotation(deltaX, deltaY);
    }
  };

  const updateCameraFromRotation = (rot: { x: number; z: number }) => {
    // Normalize rotations to 0-360 range
    let x = ((rot.x % 360) + 360) % 360;
    let z = ((rot.z % 360) + 360) % 360;

    // Determine which view the cube is facing
    // This is a simplified approach: find which direction is closest to front
    // A more sophisticated approach would use quaternions
    
    // Check which axis is dominant to determine view
    let view: 'front' | 'back' | 'left' | 'right' | 'top' | 'bottom' | 'iso' = 'iso';

    // Determine view based on rotation
    if (z > 315 || z < 45) {
      // Front-ish
      if (x > 315 || x < 45) {
        view = 'front';
      } else if (x > 135 && x < 225) {
        view = 'bottom';
      } else {
        view = 'iso';
      }
    } else if (z > 135 && z < 225) {
      // Back-ish
      view = 'back';
    } else if (z > 45 && z < 135) {
      // Right-ish
      view = 'right';
    } else if (z > 225 && z < 315) {
      // Left-ish
      view = 'left';
    }

    onViewChange(view);
  };

  const handleMouseUp = () => {
    setIsDragging(false);
  };

  return (
    <div className="absolute top-4 right-4 pointer-events-auto select-none">
      <div 
        ref={containerRef}
        className="relative w-40 h-40 cursor-grab active:cursor-grabbing"
        style={{
          perspective: '1000px',
        }}
        onMouseDown={handleMouseDown}
        onMouseMove={handleMouseMove}
        onMouseUp={handleMouseUp}
        onMouseLeave={handleMouseUp}
      >
        {/* Cube container - rotatable via drag */}
        <div 
          className="relative w-full h-full"
          style={{
            transformStyle: 'preserve-3d',
            transform: `rotateX(${rotation.x}deg) rotateZ(${rotation.z}deg)`,
            transition: isDragging ? 'none' : 'transform 0.3s ease-out',
          }}
        >
          {/* Front face */}
          <button
            onClick={(e) => handleFaceClick(e, 'front')}
            onMouseEnter={() => setHoveredFace('front')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'front'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Front view, drag to rotate"
          >
            Front
          </button>

          {/* Back face */}
          <button
            onClick={(e) => handleFaceClick(e, 'back')}
            onMouseEnter={() => setHoveredFace('back')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'back'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'rotateY(180deg) translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Back view, drag to rotate"
          >
            Back
          </button>

          {/* Right face */}
          <button
            onClick={(e) => handleFaceClick(e, 'right')}
            onMouseEnter={() => setHoveredFace('right')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'right'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'rotateY(90deg) translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Right view, drag to rotate"
          >
            Right
          </button>

          {/* Left face */}
          <button
            onClick={(e) => handleFaceClick(e, 'left')}
            onMouseEnter={() => setHoveredFace('left')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'left'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'rotateY(-90deg) translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Left view, drag to rotate"
          >
            Left
          </button>

          {/* Top face */}
          <button
            onClick={(e) => handleFaceClick(e, 'top')}
            onMouseEnter={() => setHoveredFace('top')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'top'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'rotateX(90deg) translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Top view, drag to rotate"
          >
            Top
          </button>

          {/* Bottom face */}
          <button
            onClick={(e) => handleFaceClick(e, 'bottom')}
            onMouseEnter={() => setHoveredFace('bottom')}
            onMouseLeave={() => setHoveredFace(null)}
            className={`absolute w-full h-full flex items-center justify-center font-bold text-xs rounded transition-all ${
              hoveredFace === 'bottom'
                ? 'bg-blue-500 text-white shadow-lg'
                : 'bg-blue-400 text-white hover:bg-blue-500'
            }`}
            style={{
              transform: 'rotateX(-90deg) translateZ(80px)',
              border: '1px solid #1e40af',
              pointerEvents: isDragging ? 'none' : 'auto',
            }}
            title="Click for Bottom view, drag to rotate"
          >
            Bottom
          </button>
        </div>
      </div>
    </div>
  );
};

export default ViewCube3D;
