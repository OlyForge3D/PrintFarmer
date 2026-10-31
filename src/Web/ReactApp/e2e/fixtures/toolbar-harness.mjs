import { createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { ToolbarConsistencyFixture } from '/src/test/fixtures/toolbar-consistency.tsx';

export function mountToolbarFixture(element) {
  createRoot(element).render(createElement(ToolbarConsistencyFixture));
}
