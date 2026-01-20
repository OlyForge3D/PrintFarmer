import { useState, useCallback } from 'react';

interface MenuPosition {
  x: number;
  y: number;
}

export function useContextMenu() {
  const [menuPosition, setMenuPosition] = useState<MenuPosition | null>(null);

  const handleContextMenu = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    setMenuPosition({
      x: e.clientX,
      y: e.clientY,
    });
  }, []);

  const closeMenu = useCallback(() => {
    setMenuPosition(null);
  }, []);

  return {
    isOpen: menuPosition !== null,
    position: menuPosition,
    handleContextMenu,
    closeMenu,
  };
}
