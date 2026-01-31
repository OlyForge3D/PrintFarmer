import React from 'react';
import { BrowserRouter } from 'react-router';

// Shared test router for use in tests.
// React Router v7 no longer requires future flags (they are now default behavior).
export function TestRouter({ children }: { children: React.ReactNode }) {
  return <BrowserRouter>{children}</BrowserRouter>;
}
