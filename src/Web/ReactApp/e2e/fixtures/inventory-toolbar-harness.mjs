import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { InventoryToolbarFixture } from '/src/test/fixtures/inventory-toolbar.tsx';

export function mountInventoryToolbarFixture(element, surface = 'parts') {
  const root = createRoot(element);
  root.render(createElement(InventoryToolbarFixture, { surface }));
  return () => root.unmount();
}
